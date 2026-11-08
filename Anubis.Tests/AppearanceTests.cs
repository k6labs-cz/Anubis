using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Anubis.Tests.Support;

using k6labs.Anubis.Data;
using k6labs.Anubis.Options;
using k6labs.Anubis.Services;

using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

/// <summary>
/// Appearance from the Anubis:Appearance configuration: Hathor theme tokens on &lt;body&gt;, logo/favicon
/// in the page, CSP allowing remote avatars, and the default Gravatar avatar.
/// </summary>
[Collection(AnubisCollection.Name)]
public class AppearanceTests
{
    private readonly AnubisFactory _factory;

    public AppearanceTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ReferrerPolicy_KeepsTheOriginOnSameSiteFormPosts()
    {
        using var client = _factory.CreateAnonymousClient();

        using var response = await client.GetAsync("/login");

        // "no-referrer" makes browsers send "Origin: null" on form posts, which breaks
        // passkey registration and sign-in (the Origin header must match the WebAuthn origin).
        Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task ConfiguredColorsAndFont_AreHathorTokensOnTheBody()
    {
        using var client = _factory.CreateAnonymousClient();

        var html = await client.GetStringAsync("/login");

        var body = Regex.Match(html, "<body[^>]*>").Value;
        Assert.Contains("class=\"hathor\"", body);
        Assert.Contains("--hathor-light-primary:#123456", body);     // Anubis__Appearance__Light__Primary
        Assert.Contains("--hathor-dark-bg:#000001", body);           // Anubis__Appearance__Dark__Background
        Assert.Contains("--font-family:TestFont, sans-serif", body); // Anubis__Appearance__FontFamily
    }

    [Fact]
    public async Task Ui_UsesOnlyHathorStyles_WithoutOwnStylesheetsOrFonts()
    {
        var project = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Anubis");
        Assert.Empty(Directory.GetFiles(Path.Combine(project, "wwwroot"), "*.css", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(project, "Components"), "*.razor.css", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(project, "wwwroot", "fonts")));

        using var client = _factory.CreateAnonymousClient();
        var html = await client.GetStringAsync("/login");

        // The only stylesheet is the scoped CSS bundle (Hathor's styles).
        Assert.Equal(["k6labs.Anubis.styles.css"], Regex.Matches(html, "<link rel=\"stylesheet\" href=\"([^\"]+)\"").Select(match => match.Groups[1].Value));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/css/appearance.css")).StatusCode);
    }

    [Fact]
    public async Task DefaultLogoAndFavicon_AreInAppsettings_NotInWwwroot()
    {
        var project = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Anubis");
        var appsettings = File.ReadAllText(Path.Combine(project, "appsettings.json"));

        Assert.Contains(AppearanceOptions.DefaultLogo, appsettings);
        Assert.Contains(AppearanceOptions.DefaultFavicon, appsettings);
        Assert.False(File.Exists(Path.Combine(project, "wwwroot", "favicon.svg")));
        Assert.False(File.Exists(Path.Combine(project, "wwwroot", "img", "logo.svg")));

        using var client = _factory.CreateAnonymousClient();
        using var logo = await client.GetAsync($"/appearance/logo?v={AppearanceAssets.Version(AppearanceOptions.DefaultLogo)}");
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/svg+xml", logo.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task LoginPage_UsesDefaultLogoAndFavicon()
    {
        using var client = _factory.CreateAnonymousClient();

        using var response = await client.GetAsync("/login");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The defaults are data URLs in the configuration, served from /appearance/* (no files in wwwroot).
        Assert.Contains($"href=\"/appearance/favicon?v={AppearanceAssets.Version(AppearanceOptions.DefaultFavicon)}\"", html);
        Assert.Contains($"src=\"/appearance/logo?v={AppearanceAssets.Version(AppearanceOptions.DefaultLogo)}\"", html);
        Assert.DoesNotContain("data:image", html);
        Assert.Contains("k6labs.Anubis.styles.css", html);
        Assert.Contains("class=\"hathor\"", html);
        Assert.DoesNotContain("_content/MudBlazor", html);
        // Avatars (Gravatar / custom URL) must be allowed by the CSP.
        var csp = string.Join(" ", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("img-src 'self' data: https:", csp);
    }

    [Fact]
    public async Task Footer_HasOnlyTheCenteredCopyright()
    {
        using var client = _factory.CreateAnonymousClient();

        var html = await client.GetStringAsync("/login");

        var footer = Regex.Match(html, "<footer[^>]*>.*?</footer>", RegexOptions.Singleline).Value;
        Assert.Contains("justify--center", footer);
        Assert.Contains("© 2006", footer);
        Assert.Contains("href=\"https://k6labs.cz\"", footer);
        Assert.DoesNotContain("<form", footer);             // no language or theme switcher
        Assert.DoesNotContain("<button", footer);
    }

    [Fact]
    public async Task AccountPage_ShowsGravatarAvatar_WhenNoCustomAvatarIsSet()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();

        var html = await client.Http.GetStringAsync("/profile");

        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(user.Email!.Trim().ToLowerInvariant()))).ToLowerInvariant();
        Assert.Contains($"https://www.gravatar.com/avatar/{hash}", html);
        // Account page has no <h1> heading and renders the app frame with one navigation.
        Assert.DoesNotContain("<h1", html);
        // Navigation: once as the desktop column, once in the phone drawer behind the menu button.
        Assert.Contains("aria-label=\"Main navigation\"", html);
        Assert.Equal(2, Regex.Matches(html, "<nav [^>]*aria-label=\"Account\"").Count);
        Assert.Contains("href=\"/passkeys\"", html);
        Assert.Contains("href=\"/mfa\"", html);
        Assert.Contains("href=\"/change-password\"", html);
        Assert.DoesNotContain("/account/permissions", html);
        Assert.Contains("action=\"/auth/logout\"", html);
        Assert.Contains("href=\"/logout\"", html);
        // Management and Settings are for administrators only.
        Assert.DoesNotContain("href=\"/users\"", html);
        Assert.DoesNotContain("href=\"/options-general\"", html);
    }

    [Fact]
    public async Task AdminAccountPage_ShowsManagementAndSettingsInTheSameNavigation()
    {
        using var client = new FormClient(_factory);
        (await client.PasswordLoginAsync(AnubisFactory.AdminEmail, AnubisFactory.AdminPassword)).Dispose();

        var html = await client.Http.GetStringAsync("/profile");

        Assert.Equal(2, Regex.Matches(html, "<nav [^>]*aria-label=\"Management\"").Count);
        Assert.Equal(2, Regex.Matches(html, "<nav [^>]*aria-label=\"Settings\"").Count);
        // Order: account, Management, Settings, then signing out.
        var order = new[] { "href=\"/history\"", "href=\"/users\"", "href=\"/options-general\"", "href=\"/logout\"" }
            .Select(link => html.IndexOf(link, StringComparison.Ordinal)).ToArray();
        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.Order().ToArray(), order);
    }

    [Fact]
    public async Task AccountPage_AllowsUpdatingDisplayName()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();

        var token = await client.GetTokenAsync("/profile");
        using var response = await client.Http.PostAsync("/auth/profile", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["displayName"] = "Anubis Jackal",
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var html = await client.Http.GetStringAsync("/profile");
        Assert.Contains("Anubis Jackal", html);
    }

    [Fact]
    public async Task AvatarUpload_ResizesToMax1024x1024_SavesAsTransparentWebpInSqlite_AndResetFallsBackToGravatar()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();

        // Create a 1600x1200 PNG with a transparent top-left corner and an opaque cyan center.
        byte[] pngBytes;
        using (var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(1600, 1200, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul)))
        {
            bitmap.Erase(SkiaSharp.SKColors.Transparent);
            using (var canvas = new SkiaSharp.SKCanvas(bitmap))
            using (var paint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0, 200, 230, 255) })
            {
                canvas.DrawRect(400, 300, 800, 600, paint);
            }

            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            pngBytes = encoded.ToArray();
        }

        var token = await client.GetTokenAsync("/profile");
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent(token), "__RequestVerificationToken");
            var fileContent = new ByteArrayContent(pngBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(fileContent, "avatar", "avatar.png");

            using var uploadResponse = await client.Http.PostAsync("/auth/avatar", form);
            Assert.Equal(HttpStatusCode.Redirect, uploadResponse.StatusCode);
            Assert.Equal("/profile?status=avatar-updated", uploadResponse.Headers.Location?.OriginalString);
        }

        // Verify the UserAvatars row in SQLite is resized to 1024x768 and stored as transparent WebP.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnubisDbContext>();
            var saved = await db.UserAvatars.FindAsync(user.Id);
            Assert.NotNull(saved);
            Assert.Equal("image/webp", saved.ContentType);
            Assert.Equal(1024, saved.Width);
            Assert.Equal(768, saved.Height);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(saved.Data, 0, 4));
            Assert.Equal("WEBP", Encoding.ASCII.GetString(saved.Data, 8, 4));

            using var decoded = SkiaSharp.SKBitmap.Decode(saved.Data);
            Assert.Equal(1024, decoded.Width);
            Assert.Equal(768, decoded.Height);
            Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
            Assert.True(decoded.GetPixel(512, 384).Alpha > 200);
        }

        // Verify the avatar endpoint serves the WebP image and /profile links to it instead of Gravatar.
        using (var avatarResponse = await client.Http.GetAsync($"/avatars/{user.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, avatarResponse.StatusCode);
            Assert.Equal("image/webp", avatarResponse.Content.Headers.ContentType?.MediaType);
        }

        var accountHtml = await client.Http.GetStringAsync("/profile");
        Assert.Contains($"/avatars/{user.Id}?v=", accountHtml);
        Assert.DoesNotContain("gravatar.com", accountHtml);

        // Removing the custom avatar deletes the DB row and reverts /profile to Gravatar.
        token = await client.GetTokenAsync("/profile");
        using (var deleteResponse = await client.Http.PostAsync("/auth/avatar/delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        })))
        {
            Assert.Equal(HttpStatusCode.Redirect, deleteResponse.StatusCode);
            Assert.Equal("/profile?status=avatar-removed", deleteResponse.Headers.Location?.OriginalString);
        }

        var revertedHtml = await client.Http.GetStringAsync("/profile");
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(user.Email!.Trim().ToLowerInvariant()))).ToLowerInvariant();
        Assert.Contains($"https://www.gravatar.com/avatar/{hash}", revertedHtml);
    }
}
