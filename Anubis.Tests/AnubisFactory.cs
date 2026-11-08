using k6labs.Anubis.Services.Email;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using CapturingEmailTransport = Anubis.Tests.Support.CapturingEmailTransport;

namespace Anubis.Tests;

/// <summary>
/// Boots the real application in the "Testing" environment with an isolated
/// SQLite database file and two seeded clients:
/// a confidential client-credentials service and a web client using the code flow.
/// Configuration is applied via environment variables so it reliably overrides
/// appsettings.json (which also defines ConnectionStrings and Anubis defaults).
/// </summary>
public class AnubisFactory : WebApplicationFactory<Program>
{
    public const string ServiceClientId = "test-service";
    public const string ServiceClientSecret = "test-service-secret";
    public const string WebClientId = "test-web";
    public const string WebClientSecret = "test-web-secret";
    public const string WebClientRedirectUri = "https://client.example.com/callback";
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Test-Admin-Password-123!";

    /// <summary>Keys (Data Protection, OpenIddict certificates) of the test host, never the developer's ones.</summary>
    public static readonly string DataDirectory = Path.Combine(Path.GetTempPath(), $"anubis-tests-data-{Guid.NewGuid():N}");

    /// <summary>Settings saved from the admin UI go to a temp file, never to the developer's one.</summary>
    public static readonly string SettingsFilePath = Path.Combine(Path.GetTempPath(), $"anubis-tests-settings-{Guid.NewGuid():N}.json");

    static AnubisFactory()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"anubis-tests-{Guid.NewGuid():N}.db");

        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={databasePath}");
        Environment.SetEnvironmentVariable("Anubis__DataDirectory", DataDirectory);
        Environment.SetEnvironmentVariable("Anubis__SettingsFile", SettingsFilePath);
        Environment.SetEnvironmentVariable("Anubis__AdminEmail", AdminEmail);
        Environment.SetEnvironmentVariable("Anubis__AdminPassword", AdminPassword);

        Environment.SetEnvironmentVariable("Anubis__Clients__0__ClientId", ServiceClientId);
        Environment.SetEnvironmentVariable("Anubis__Clients__0__ClientSecret", ServiceClientSecret);
        Environment.SetEnvironmentVariable("Anubis__Clients__0__ClientType", "confidential");
        Environment.SetEnvironmentVariable("Anubis__Clients__0__GrantTypes__0", "client_credentials");
        Environment.SetEnvironmentVariable("Anubis__Clients__0__Scopes__0", "anubis-api");
        Environment.SetEnvironmentVariable("Anubis__Clients__0__RequireConsent", "false");

        Environment.SetEnvironmentVariable("Anubis__Clients__1__ClientId", WebClientId);
        Environment.SetEnvironmentVariable("Anubis__Clients__1__ClientSecret", WebClientSecret);
        Environment.SetEnvironmentVariable("Anubis__Clients__1__ClientType", "confidential");
        Environment.SetEnvironmentVariable("Anubis__Clients__1__RedirectUris__0", WebClientRedirectUri);
        Environment.SetEnvironmentVariable("Anubis__Clients__1__PostLogoutRedirectUris__0", "https://client.example.com/");
        Environment.SetEnvironmentVariable("Anubis__Clients__1__GrantTypes__0", "authorization_code");
        Environment.SetEnvironmentVariable("Anubis__Clients__1__GrantTypes__1", "refresh_token");
        Environment.SetEnvironmentVariable("Anubis__Clients__1__Scopes__0", "openid");
        Environment.SetEnvironmentVariable("Anubis__Clients__1__Scopes__1", "profile");
        Environment.SetEnvironmentVariable("Anubis__Clients__1__Scopes__2", "email");
        Environment.SetEnvironmentVariable("Anubis__Clients__1__RequireConsent", "true");

        // Appearance exercised by AppearanceTests (Hathor theme tokens on <body>).
        Environment.SetEnvironmentVariable("Anubis__Appearance__Light__Primary", "#123456");
        Environment.SetEnvironmentVariable("Anubis__Appearance__Dark__Background", "#000001");
        Environment.SetEnvironmentVariable("Anubis__Appearance__FontFamily", "TestFont, sans-serif");
    }

    /// <summary>Every email the app "sent" during the tests.</summary>
    public CapturingEmailTransport Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services => services.AddSingleton<IEmailTransport>(Emails));
    }

    public HttpClient CreateAnonymousClient()
        => CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
}

[CollectionDefinition(Name)]
public class AnubisCollection : ICollectionFixture<AnubisFactory>
{
    public const string Name = "Anubis";
}
