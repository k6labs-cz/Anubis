using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Anubis.Tests.Support;

using k6labs.Anubis.Options;
using k6labs.Anubis.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Anubis.Tests;

/// <summary>
/// Admin section: account-style layout with the left navigation, and the settings file
/// written by the Appearance / Email pages (applied without a restart).
/// </summary>
[Collection(AnubisCollection.Name)]
public class AdminTests
{
    private static readonly string[] NavLinks =
        ["/users", "/groups", "/tenants", "/roles", "/scopes", "/clients", "/options-general", "/options-security", "/options-tokens", "/options-appearance", "/options-email"];

    private readonly AnubisFactory _factory;

    public AdminTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/users")]
    [InlineData("/groups")]
    [InlineData("/tenants")]
    [InlineData("/roles")]
    [InlineData("/scopes")]
    [InlineData("/clients")]
    [InlineData("/options-general")]
    [InlineData("/options-security")]
    [InlineData("/options-tokens")]
    [InlineData("/options-appearance")]
    [InlineData("/options-email")]
    public async Task AdminPages_UseTheSharedAppLayout(string path)
    {
        using var client = await AdminClientAsync();

        using var response = await client.Http.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        // One navigation for the account and admin pages: once as the desktop column, once in the phone drawer.
        Assert.Contains("class=\"app-shell\"", html);
        Assert.Equal(2, Regex.Matches(html, "<nav [^>]*aria-label=\"Management\"").Count);
        Assert.Equal(2, Regex.Matches(html, "<nav [^>]*aria-label=\"Account\"").Count);
        Assert.Contains("href=\"/profile\"", html);
        Assert.Contains("href=\"/logout\"", html);
        Assert.Contains("Management", html);
        foreach (var link in NavLinks)
        {
            Assert.Contains($"href=\"{link}\"", html);
        }
    }

    [Theory]
    [InlineData("/users")]
    [InlineData("/groups")]
    [InlineData("/tenants")]
    [InlineData("/roles")]
    [InlineData("/scopes")]
    [InlineData("/clients")]
    public async Task ManagementLists_HaveRowMenus_AndACreateButton_ButNoInlineForms(string path)
    {
        using var client = await AdminClientAsync();

        var html = await client.Http.GetStringAsync(path);

        Assert.Contains("data-row-menu", html);          // "⋮" actions per row
        Assert.Contains("aria-haspopup=\"menu\"", html);
        Assert.DoesNotContain("<form", ExtractContent(html));   // creating happens in a dialog
    }

    [Fact]
    public async Task ClientEditorPage_IsGone()
    {
        using var client = await AdminClientAsync();

        using var response = await client.Http.GetAsync("/clients/new");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The page body without the layout (whose navigation has no forms either way).</summary>
    private static string ExtractContent(string html)
    {
        // The page itself: between the start of <main> and the footer (the sign-out form is outside).
        var start = html.IndexOf("app-shell__content", StringComparison.Ordinal);
        var end = html.IndexOf("app-shell__footer", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return html[start..end];
    }

    [Fact]
    public async Task ServerName_DefaultsToAnubis_AndAppearsInTitlesAndTheHeader()
    {
        var settings = _factory.Services.GetRequiredService<SettingsFile>();
        using var anonymous = _factory.CreateAnonymousClient();
        Assert.Contains("<title>Sign in — Anubis</title>", await anonymous.GetStringAsync("/login"));

        try
        {
            await settings.SaveSectionAsync(GeneralOptions.SectionName, new JsonObject { ["ServerName"] = "Contoso ID" });

            Assert.Contains("<title>Sign in — Contoso ID</title>", await anonymous.GetStringAsync("/login"));

            using var admin = await AdminClientAsync();
            foreach (var path in new[] { "/profile", "/users" })
            {
                var html = await admin.Http.GetStringAsync(path);
                Assert.Matches("data-server-name[^>]*>Contoso ID<", html);   // top left above the panel
                var logo = Regex.Match(html, "data-server-logo[^>]*><img[^>]*>").Value;   // with the logo before it
                Assert.Contains("src=\"/appearance/logo?v=", logo);
                Assert.Contains("— Contoso ID</title>", html);
            }

            Assert.Contains("value=\"Contoso ID\"", await admin.Http.GetStringAsync("/options-general"));
        }
        finally
        {
            await settings.SaveSectionAsync(GeneralOptions.SectionName, new JsonObject());
        }
    }

    [Fact]
    public async Task SettingsPages_AreAdminOnly()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();

        using var response = await client.Http.GetAsync("/options-appearance");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SavedSettings_ApplyWithoutRestart_AndWinOverEnvironment()
    {
        var settings = _factory.Services.GetRequiredService<SettingsFile>();
        Assert.Equal(AnubisFactory.SettingsFilePath, settings.Path);

        try
        {
            // Only the keys this test owns: other tests rely on the environment values.
            await settings.SaveSectionAsync(AppearanceOptions.SectionName, new JsonObject
            {
                ["Light"] = new JsonObject { ["Secondary"] = "#abcdef" },
                ["Dark"] = new JsonObject { ["Background"] = "#000002" },   // environment says #000001
            });

            var monitor = _factory.Services.GetRequiredService<IOptionsMonitor<AppearanceOptions>>();
            Assert.Equal("#abcdef", monitor.CurrentValue.Light.Secondary);

            using var client = _factory.CreateAnonymousClient();
            var body = Regex.Match(await client.GetStringAsync("/login"), "<body[^>]*>").Value;
            Assert.Contains("--hathor-light-secondary:#abcdef", body);
            Assert.Contains("--hathor-dark-bg:#000002", body);
            Assert.Contains("--hathor-light-primary:#123456", body);   // still from the environment
        }
        finally
        {
            await settings.SaveSectionAsync(AppearanceOptions.SectionName, new JsonObject());
        }
    }

    [Fact]
    public async Task SettingsFile_ReplacesOneSection_AndKeepsTheOthers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"anubis-settings-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            {
              // comments are allowed
              "email": { "FromName": "Old", "Smtp": { "Password": "keep-me" } },
              "Anubis": { "Other": "value" }
            }
            """);

        try
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: true, reloadOnChange: false).Build();
            var file = new SettingsFile(path, configuration);

            var existing = await file.ReadSectionAsync("Email");
            Assert.Equal("keep-me", existing?["Smtp"]?["Password"]?.GetValue<string>());

            await file.SaveSectionAsync("Email", SettingsFile.ToNode(new EmailOptions { FromName = "New", Provider = EmailProvider.Resend }));
            await file.SaveSectionAsync("Anubis:Appearance", new JsonObject { ["LogoPath"] = "/logo.png" });

            Assert.Equal("New", configuration["Email:FromName"]);              // reloaded
            Assert.Equal("Resend", configuration["Email:Provider"]);           // enum as text
            Assert.Null(configuration["Email:IsConfigured"]);                  // computed property skipped
            Assert.Equal("value", configuration["Anubis:Other"]);
            Assert.Equal("/logo.png", configuration["Anubis:Appearance:LogoPath"]);

            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            Assert.Single(json, p => string.Equals(p.Key, "Email", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private async Task<FormClient> AdminClientAsync()
    {
        var client = new FormClient(_factory);
        (await client.PasswordLoginAsync(AnubisFactory.AdminEmail, AnubisFactory.AdminPassword)).Dispose();
        return client;
    }
}
