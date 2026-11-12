using k6labs.Anubis.Services.Email;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using CapturingEmailTransport = Anubis.Tests.Support.CapturingEmailTransport;

namespace Anubis.Tests;

/// <summary>
/// Boots the real application in the "Testing" environment with an isolated
/// SQLite database file and seeded clients (a confidential client-credentials service,
/// a web client using the code flow, an application with roles), plus one seeded tenant
/// ("acme") with a database, an administrator and clients of its own.
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
    /// <summary>An application with roles of its own (Administrator, User), signing users in without a consent page.</summary>
    public const string AppClientId = "test-app";
    public const string AppClientSecret = "test-app-secret";
    public const string AppClientRedirectUri = "https://app.example.com/callback";
    /// <summary>A second tenant, reached at <c>acme.localhost</c> and at its own host name.</summary>
    public const string SeededTenant = "acme";
    public const string TenantHost = "acme.localhost";
    public const string TenantOwnHost = "login.acme.test";
    public const string TenantAdminEmail = "admin@acme.test";
    public const string TenantAdminPassword = "Acme-Admin-Password-123!";
    public const string TenantServiceClientId = "acme-service";
    public const string TenantServiceClientSecret = "acme-service-secret";
    public const string TenantAppClientId = "acme-app";
    public const string TenantAppClientSecret = "acme-app-secret";
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

        Environment.SetEnvironmentVariable("Anubis__Clients__2__ClientId", AppClientId);
        Environment.SetEnvironmentVariable("Anubis__Clients__2__ClientSecret", AppClientSecret);
        Environment.SetEnvironmentVariable("Anubis__Clients__2__RedirectUris__0", AppClientRedirectUri);
        Environment.SetEnvironmentVariable("Anubis__Clients__2__GrantTypes__0", "authorization_code");
        Environment.SetEnvironmentVariable("Anubis__Clients__2__GrantTypes__1", "client_credentials");
        Environment.SetEnvironmentVariable("Anubis__Clients__2__Scopes__0", "openid");
        Environment.SetEnvironmentVariable("Anubis__Clients__2__Scopes__1", "profile");
        Environment.SetEnvironmentVariable("Anubis__Clients__2__Scopes__2", "roles");
        Environment.SetEnvironmentVariable("Anubis__Clients__2__RequireConsent", "false");
        Environment.SetEnvironmentVariable("Anubis__Clients__2__Roles__0__Name", "Administrator");
        Environment.SetEnvironmentVariable("Anubis__Clients__2__Roles__0__Description", "Changes the settings");
        Environment.SetEnvironmentVariable("Anubis__Clients__2__Roles__1__Name", "User");
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Id", SeededTenant);
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__DisplayName", "Acme Ltd.");
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Hosts__0", TenantOwnHost);
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__AdminEmail", TenantAdminEmail);
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__AdminPassword", TenantAdminPassword);
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__0__ClientId", TenantServiceClientId);
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__0__ClientSecret", TenantServiceClientSecret);
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__0__GrantTypes__0", "client_credentials");
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__0__Scopes__0", "anubis-api");
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__1__ClientId", TenantAppClientId);
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__1__ClientSecret", TenantAppClientSecret);
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__1__RedirectUris__0", AppClientRedirectUri);
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__1__GrantTypes__0", "authorization_code");
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__1__GrantTypes__1", "refresh_token");
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__1__GrantTypes__2", "client_credentials");
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__1__Scopes__0", "openid");
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__1__Scopes__1", "roles");
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__1__Scopes__2", "offline_access");
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__1__RequireConsent", "false");
        Environment.SetEnvironmentVariable("Anubis__Tenants__0__Clients__1__Roles__0__Name", "Manager");

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

    /// <param name="host">The host name the client calls: null is the default tenant, <see cref="TenantHost"/> the seeded one.</param>
    public HttpClient CreateAnonymousClient(string? host = null)
        => CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri($"http://{host ?? "localhost"}"),
        });

    /// <summary>The tenant with the id, as the server knows it.</summary>
    public k6labs.Anubis.Tenancy.Tenant Tenant(string id)
        => Services.GetRequiredService<k6labs.Anubis.Tenancy.TenantCatalog>().Find(id) ?? throw new InvalidOperationException($"No tenant '{id}'.");
}

[CollectionDefinition(Name)]
public class AnubisCollection : ICollectionFixture<AnubisFactory>
{
    public const string Name = "Anubis";
}
