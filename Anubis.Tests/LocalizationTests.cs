using System.Globalization;
using System.Text.RegularExpressions;

using Anubis.Tests.Support;

using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Localization;
using k6labs.Anubis.Resources;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

/// <summary>EN is the default; CS must be complete. Language comes from the user's choice or browser.</summary>
[Collection(AnubisCollection.Name)]
public partial class LocalizationTests
{
    private static readonly CultureInfo Czech = CultureInfo.GetCultureInfo("cs");

    private readonly AnubisFactory _factory;

    public LocalizationTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    // ---------------------------------------------------------------- completeness

    public static TheoryData<string> TranslatedLanguages()
        => new(Cultures.Supported.Where(code => code != Cultures.Default));

    [Fact]
    public void EveryOfficialEuLanguage_IsSupported()
    {
        string[] eu = ["bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "ga", "hr", "hu", "it", "lt", "lv", "mt", "nl", "pl", "pt", "ro", "sk", "sl", "sv"];

        Assert.Equal(eu, Cultures.Supported.Order());

        // Every language has its own name in the switcher (unknown codes fall back to "English").
        var names = Cultures.Supported.Select(Cultures.NativeName).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(TranslatedLanguages))]
    public void Translations_CoverEveryEnglishString(string code)
    {
        var english = ReadSet(CultureInfo.InvariantCulture);
        var translated = ReadSet(CultureInfo.GetCultureInfo(code));

        Assert.NotEmpty(english);
        Assert.Empty(english.Keys.Except(translated.Keys));   // missing in this language
        Assert.Empty(translated.Keys.Except(english.Keys));   // stale in this language
        Assert.Empty(english.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key));
        Assert.Empty(translated.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key));

        // Same {0}, {1} ... placeholders, or string.Format would throw or drop values.
        var mismatched = english.Keys.Where(key => !Placeholders(english[key]).SetEquals(Placeholders(translated[key]))).ToList();
        Assert.Empty(mismatched);
    }

    [Fact]
    public void RazorMarkup_HasNoHardcodedText()
    {
        var componentsDir = Path.Combine(RepoRoot(), "Anubis", "Components");
        var findings = new List<string>();

        foreach (var file in Directory.EnumerateFiles(componentsDir, "*.razor", SearchOption.AllDirectories))
        {
            var markup = MarkupOnly(File.ReadAllText(file));
            var name = Path.GetRelativePath(componentsDir, file);

            foreach (Match match in TextNodeRegex().Matches(markup))
            {
                var text = EntityRegex().Replace(match.Groups[1].Value, string.Empty).Trim();
                if (LooksLikeText(text) && !AllowedText.Contains(text) && !TechnicalRegex().IsMatch(text))
                {
                    findings.Add($"{name}: \"{text}\"");
                }
            }

            foreach (Match match in TextAttributeRegex().Matches(markup))
            {
                var value = match.Groups[2].Value;
                if (LooksLikeText(value) && !ExampleValueRegex().IsMatch(value))
                {
                    findings.Add($"{name}: {match.Groups[1].Value}=\"{value}\"");
                }
            }
        }

        // Move user-visible text to Resources/Translations.resx (+ Translations.cs.resx) and use @Translations.Key.
        Assert.Empty(findings);
    }

    // ---------------------------------------------------------------- language selection

    [Fact]
    public async Task Pages_DefaultToEnglish()
    {
        using var client = new FormClient(_factory);

        var html = await client.Http.GetStringAsync("/login");

        Assert.Contains("<html lang=\"en\"", html);
        Assert.Contains(English(nameof(Translations.Login_Title)), html);
    }

    [Fact]
    public async Task Pages_FollowBrowserLanguage()
    {
        using var client = new FormClient(_factory, "cs-CZ,cs;q=0.9,en;q=0.8");

        var html = await client.Http.GetStringAsync("/login");

        Assert.Contains("<html lang=\"cs\"", html);
        Assert.Contains(Html(InCzech(nameof(Translations.Login_Title))), html);
        Assert.DoesNotContain(English(nameof(Translations.Login_Title)), html);
    }

    [Theory]
    [MemberData(nameof(TranslatedLanguages))]
    public async Task EveryLanguage_RendersTheSignInPage(string code)
    {
        using var client = new FormClient(_factory, code);

        var html = await client.Http.GetStringAsync("/login");

        Assert.Contains($"<html lang=\"{code}\"", html);
        var title = Translations.ResourceManager.GetString(nameof(Translations.Login_Title), CultureInfo.GetCultureInfo(code))!;
        Assert.Contains(Html(title), html);
    }

    [Fact]
    public async Task UnsupportedBrowserLanguage_FallsBackToEnglish()
    {
        using var client = new FormClient(_factory, "ja-JP");   // not an EU language

        var html = await client.Http.GetStringAsync("/login");

        Assert.Contains("<html lang=\"en\"", html);
    }

    [Fact]
    public async Task OidcUiLocales_PicksTheLanguage()
    {
        using var client = new FormClient(_factory, "en");
        var returnUrl = Uri.EscapeDataString("/authorize?client_id=x&ui_locales=cs%20en");

        var html = await client.Http.GetStringAsync($"/login?returnUrl={returnUrl}");

        Assert.Contains("<html lang=\"cs\"", html);
    }

    [Fact]
    public async Task SavedAccountLanguage_WinsOverTheBrowserLanguage()
    {
        using var client = new FormClient(_factory, "en");
        var (user, password) = await client.CreateUserAsync();
        await client.PasswordLoginAsync(user.Email!, password);
        Assert.Contains("<html lang=\"en\"", await client.Http.GetStringAsync("/profile"));

        await SetLanguageAsync(user.Id, "cs");

        Assert.Contains("<html lang=\"cs\"", await client.Http.GetStringAsync("/profile"));
    }

    [Fact]
    public async Task SavedAccountLanguage_AppliesOnANewDevice()
    {
        using var client = new FormClient(_factory, "en");
        var (user, password) = await client.CreateUserAsync();
        await SetLanguageAsync(user.Id, "cs");

        await client.PasswordLoginAsync(user.Email!, password);

        Assert.Contains("<html lang=\"cs\"", await client.Http.GetStringAsync("/profile"));
    }

    [Fact]
    public async Task OldLanguageCookie_IsIgnored()
    {
        using var client = new FormClient(_factory, "en");
        client.Http.DefaultRequestHeaders.Add("Cookie", "anubis.culture=c%3Dcs%7Cuic%3Dcs");

        Assert.Contains("<html lang=\"en\"", await client.Http.GetStringAsync("/login"));
    }

    // ---------------------------------------------------------------- translated messages

    [Fact]
    public async Task ValidationMessages_AreTranslated()
    {
        using var client = new FormClient(_factory, "cs");

        using var response = await client.PasswordLoginAsync(string.Empty, string.Empty);

        Assert.Contains(Html(InCzech(nameof(Translations.Validation_EmailRequired))), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task IdentityErrors_AreTranslated()
    {
        using var client = new FormClient(_factory, "cs");
        var email = $"weak-{Guid.NewGuid():N}@test.local";

        // Long enough for the form, but Identity wants a digit and an uppercase letter.
        using var response = await client.PostFormAsync("/register", "register", new Dictionary<string, string>
        {
            ["model.Email"] = email,
            ["model.Password"] = "onlylowercaseletters",
            ["model.ConfirmPassword"] = "onlylowercaseletters",
        });
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains(Html(InCzech(nameof(Translations.Identity_PasswordRequiresDigit))), html);
        Assert.Contains(Html(InCzech(nameof(Translations.Identity_PasswordRequiresUpper))), html);
    }

    [Fact]
    public async Task Emails_UseTheRecipientsLanguage()
    {
        // The request is English, but the account is set to Czech.
        using var client = new FormClient(_factory, "en");
        var (user, _) = await client.CreateUserAsync();
        await SetLanguageAsync(user.Id, "cs");

        (await client.PostFormAsync("/forgot-password", "forgot-password", new Dictionary<string, string>
        {
            ["model.Email"] = user.Email!,
        })).Dispose();

        var message = _factory.Emails.To(user.Email!).Single();
        Assert.Equal(InCzech(nameof(Translations.EmailReset_Subject)), message.Subject);
        Assert.Contains(InCzech(nameof(Translations.Email_LinkFallback)), message.TextBody + System.Net.WebUtility.HtmlDecode(message.HtmlBody));
        Assert.Contains("lang=\"cs\"", message.HtmlBody);
    }

    [Fact]
    public async Task Emails_WithoutSavedLanguage_UseTheRequestLanguage()
    {
        using var client = new FormClient(_factory, "cs");
        var email = $"new-{Guid.NewGuid():N}@test.local";

        (await AccountEmailTests.RegisterAsync(client, email)).Dispose();

        Assert.Equal(InCzech(nameof(Translations.EmailConfirm_Subject)), _factory.Emails.To(email).Single().Subject);
        Assert.Equal("cs", (await FindUserByEmailAsync(email))!.Language);
    }

    [Fact]
    public async Task HathorDialogCloseLabel_IsTranslated()
    {
        using var client = new FormClient(_factory);
        (await client.PasswordLoginAsync(AnubisFactory.AdminEmail, AnubisFactory.AdminPassword)).Dispose();
        client.Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("cs");
        var html = await client.Http.GetStringAsync("/users");
        Assert.Contains($"aria-label=\"{InCzech(nameof(Translations.Common_Close))}\"", System.Net.WebUtility.HtmlDecode(html));
    }

    // ---------------------------------------------------------------- helpers

    private static readonly HashSet<string> AllowedText = ["Anubis", "API", "k6labs", "TOTP", "userinfo"];

    private static Dictionary<string, string> ReadSet(CultureInfo culture)
    {
        var set = Translations.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new Xunit.Sdk.XunitException($"No resources for '{culture.Name}'.");
        return set.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static string English(string key) => Translations.ResourceManager.GetString(key, CultureInfo.InvariantCulture)!;

    private static string InCzech(string key) => Translations.ResourceManager.GetString(key, Czech)!;

    /// <summary>Razor HTML-encodes text; apostrophes and quotes become entities.</summary>
    private static string Html(string text) => HtmlEncoder.Encode(text);

    // Same settings as the app (Program.cs): non-ASCII text is not escaped.
    private static readonly System.Text.Encodings.Web.HtmlEncoder HtmlEncoder =
        System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All);

    private static HashSet<string> Placeholders(string value) => PlaceholderRegex().Matches(value).Select(m => m.Value).ToHashSet();

    private static bool LooksLikeText(string text)
        => WordRegex().IsMatch(text) && !text.Contains(';') && !text.Contains("==") && !text.Contains('?');

    /// <summary>The markup part of a component, without @code, comments and directive/control lines.</summary>
    private static string MarkupOnly(string razor)
    {
        var markup = razor.Split("\n@code")[0];
        markup = RazorCommentRegex().Replace(markup, string.Empty);
        return string.Join('\n', markup.Split('\n').Where(line => !DirectiveLineRegex().IsMatch(line)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anubis.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root (Anubis.slnx) not found.");
    }

    private async Task<User?> FindUserAsync(string id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(id);
    }

    private Task<User?> FindUserByEmailAsync(string email) => AccountEmailTests.FindUserAsync(_factory, email);

    private async Task SetLanguageAsync(string userId, string language)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await userManager.FindByIdAsync(userId))!;
        user.Language = language;
        await userManager.UpdateAsync(user);
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"[A-Za-z]{2,}")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@">([^<>@]*)<")]
    private static partial Regex TextNodeRegex();

    [GeneratedRegex(@"&[a-z]+;")]
    private static partial Regex EntityRegex();

    [GeneratedRegex(@"^(GET|POST|/)")]
    private static partial Regex TechnicalRegex();

    [GeneratedRegex(@"\b(Placeholder|placeholder|alt|title|aria-label)=""([^""@]*)""")]
    private static partial Regex TextAttributeRegex();

    // URLs, identifiers like "my-api" and code masks like "XXXXX-XXXXX" are the same in every language.
    [GeneratedRegex(@"^(https?://\S+|[a-z-]+|X+-X+)$")]
    private static partial Regex ExampleValueRegex();

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorCommentRegex();

    [GeneratedRegex(@"^\s*(@(page|using|inject|attribute|rendermode|namespace|inherits|layout|if|foreach|switch|else)\b|else\b|case |break;|var |\{|\})")]
    private static partial Regex DirectiveLineRegex();
}
