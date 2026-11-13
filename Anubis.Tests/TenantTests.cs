using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Anubis.Tests.Support;

using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Options;
using k6labs.Anubis.Services;
using k6labs.Anubis.Tenancy;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Anubis.Tests;

/// <summary>
/// Tenants: each has a database, settings and an address of its own. The tenant of a request is the one
/// its host name belongs to, and nothing of one tenant (accounts, sessions, clients, tokens, settings)
/// works in another. Tenants are created, changed and deleted from the default tenant.
/// </summary>
[Collection(AnubisCollection.Name)]
public class TenantTests
{
    private const string Acme = AnubisFactory.SeededTenant;

    private readonly AnubisFactory _factory;

    public TenantTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    private TenantAdministration Tenants => _factory.Services.GetRequiredService<TenantAdministration>();

    private TenantCatalog Catalog => _factory.Services.GetRequiredService<TenantCatalog>();

    private static string TenantFile(string tenant, string file) => Path.Combine(AnubisFactory.DataDirectory, "tenants", tenant, file);

    /// <summary>Runs <paramref name="action"/> with the services (database, managers, options) of a tenant.</summary>
    private async Task<T> InTenantAsync<T>(string tenant, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateTenantScope(_factory.Tenant(tenant));
        return await action(scope.ServiceProvider);
    }

    // ---------------------------------------------------------------- databases

    [Fact]
    public async Task SeededTenant_HasADatabaseOfItsOwn_WithItsAdministratorAndClients()
    {
        Assert.True(File.Exists(TenantFile(Acme, "anubis.db")));
        Assert.True(File.Exists(Path.Combine(AnubisFactory.DataDirectory, "anubis.tenants.db")));

        var tenant = _factory.Tenant(Acme);
        Assert.Equal("Acme Ltd.", tenant.DisplayName);
        Assert.Equal([AnubisFactory.TenantOwnHost], tenant.Hosts);

        var (acmeAdmins, acmeClients) = await InTenantAsync(Acme, ListAsync);
        var (defaultAdmins, defaultClients) = await InTenantAsync(Tenant.DefaultId, ListAsync);

        Assert.Equal([AnubisFactory.TenantAdminEmail], acmeAdmins);
        Assert.Contains(AnubisFactory.AdminEmail, defaultAdmins);
        Assert.DoesNotContain(AnubisFactory.TenantAdminEmail, defaultAdmins);

        Assert.Equal([AnubisFactory.TenantAppClientId, AnubisFactory.TenantServiceClientId], acmeClients);
        Assert.Contains(AnubisFactory.ServiceClientId, defaultClients);
        Assert.DoesNotContain(AnubisFactory.TenantServiceClientId, defaultClients);

        // The seeded role is the tenant's application's.
        var roles = await InTenantAsync(Acme, services => services.GetRequiredService<RoleAdministration>().ListRolesAsync());
        Assert.Equal(("Manager", AnubisFactory.TenantAppClientId), (roles.Single().Name, roles.Single().ClientId));

        static async Task<(List<string?> Admins, List<string?> Clients)> ListAsync(IServiceProvider services)
        {
            var admins = await services.GetRequiredService<UserManager<User>>().GetUsersInRoleAsync(DatabaseInitializer.AdminGroup);
            var clients = await services.GetRequiredService<AnubisDbContext>().Set<Client>().Select(c => c.ClientId).OrderBy(id => id).ToListAsync();
            return (admins.Select(u => u.Email).ToList(), clients);
        }
    }

    // ---------------------------------------------------------------- host names

    [Theory]
    [InlineData("localhost", Tenant.DefaultId)]
    [InlineData("acme.localhost", Acme)]                     // the subdomain of the tenant domain
    [InlineData("ACME.LocalHost.", Acme)]
    [InlineData(AnubisFactory.TenantOwnHost, Acme)]          // a host name of its own
    [InlineData("nobody.localhost", Tenant.DefaultId)]       // no such tenant
    [InlineData("acme.example.com", Tenant.DefaultId)]       // not the tenant domain
    [InlineData("deep.acme.localhost", Tenant.DefaultId)]
    [InlineData("id.example.com", Tenant.DefaultId)]
    public void HostName_DecidesTheTenant(string host, string tenant)
        => Assert.Equal(tenant, Catalog.Resolve(host).Id);

    [Theory]
    [InlineData("localhost", "Anubis", "http://localhost/")]
    [InlineData("acme.localhost", "Acme Ltd.", "http://acme.localhost/")]
    [InlineData(AnubisFactory.TenantOwnHost, "Acme Ltd.", "http://login.acme.test/")]
    public async Task Request_IsServedByTheTenantOfItsHost_UnderItsOwnIssuer(string host, string serverName, string issuer)
    {
        using var client = _factory.CreateAnonymousClient(host);

        // The tenant's display name is the default name of its server.
        Assert.Contains($"— {serverName}</title>", await client.GetStringAsync("/login"));

        using var discovery = await client.GetFromJsonAsync<JsonDocument>("/.well-known/openid-configuration");
        Assert.Equal(issuer, discovery!.RootElement.GetProperty("issuer").GetString());
        Assert.Equal($"{issuer}token", discovery.RootElement.GetProperty("token_endpoint").GetString());
    }

    [Fact]
    public async Task EveryTenant_SignsWithKeysOfItsOwn()
    {
        Assert.NotEmpty(Directory.GetFiles(TenantFile(Acme, "keys"), "signing-*.pfx"));
        Assert.NotEmpty(Directory.GetFiles(TenantFile(Acme, "keys"), "encryption-*.pfx"));

        var acme = await KeyIdsAsync(AnubisFactory.TenantHost);
        Assert.NotEmpty(acme);
        Assert.Equal(acme, await KeyIdsAsync(AnubisFactory.TenantOwnHost));   // the same tenant at its other address
        Assert.Empty(acme.Intersect(await KeyIdsAsync("localhost")));

        async Task<List<string?>> KeyIdsAsync(string host)
        {
            using var client = _factory.CreateAnonymousClient(host);
            using var keys = await client.GetFromJsonAsync<JsonDocument>("/.well-known/jwks");
            return keys!.RootElement.GetProperty("keys").EnumerateArray().Select(key => key.GetProperty("kid").GetString()).Order().ToList();
        }
    }

    [Fact]
    public async Task PublicAddress_OfATenant_MustLeadToThatTenant()
    {
        try
        {
            // acme claims the default tenant's address (or anything that is not acme's) as its issuer: ignored.
            foreach (var foreign in new[] { "https://localhost", "https://id.example.com", "not a url" })
            {
                await SetIssuerAsync(foreign);
                Assert.Equal("http://acme.localhost/", await IssuerAsync(AnubisFactory.TenantHost));
            }

            // Its own host name is fine, and applies at once.
            await SetIssuerAsync($"https://{AnubisFactory.TenantOwnHost}");
            Assert.Equal($"https://{AnubisFactory.TenantOwnHost}/", await IssuerAsync(AnubisFactory.TenantHost));
            Assert.Equal("http://localhost/", await IssuerAsync("localhost"));
        }
        finally
        {
            await InTenantAsync(Acme, async services =>
            {
                await services.GetRequiredService<SettingsFile>().SaveSectionAsync(AnubisOptions.SectionName, []);
                return true;
            });
        }

        Assert.Equal("http://acme.localhost/", await IssuerAsync(AnubisFactory.TenantHost));

        Task<bool> SetIssuerAsync(string issuer) => InTenantAsync(Acme, async services =>
        {
            await services.GetRequiredService<SettingsFile>().UpdateSectionAsync(AnubisOptions.SectionName, new System.Text.Json.Nodes.JsonObject { [nameof(AnubisOptions.Issuer)] = issuer });
            return true;
        });

        async Task<string?> IssuerAsync(string host)
        {
            using var client = _factory.CreateAnonymousClient(host);
            using var discovery = await client.GetFromJsonAsync<JsonDocument>("/.well-known/openid-configuration");
            return discovery!.RootElement.GetProperty("issuer").GetString();
        }
    }

    // ---------------------------------------------------------------- accounts and sessions

    [Fact]
    public async Task Accounts_AreTheTenantsOwn_AndASessionOfOneTenantIsNoneInAnother()
    {
        // The default tenant's administrator is nobody in acme, and the other way round.
        using (var stranger = new FormClient(_factory, tenant: Acme))
        {
            (await stranger.PasswordLoginAsync(AnubisFactory.AdminEmail, AnubisFactory.AdminPassword)).Dispose();
            Assert.False(await stranger.IsSignedInAsync());
        }

        using (var stranger = new FormClient(_factory))
        {
            (await stranger.PasswordLoginAsync(AnubisFactory.TenantAdminEmail, AnubisFactory.TenantAdminPassword)).Dispose();
            Assert.False(await stranger.IsSignedInAsync());
        }

        // The same e-mail can be an account in both: two people, two passwords.
        using var inAcme = new FormClient(_factory, tenant: Acme);
        var (user, password) = await inAcme.CreateUserAsync();
        Assert.Null(await InTenantAsync(Tenant.DefaultId, services => services.GetRequiredService<UserManager<User>>().FindByEmailAsync(user.Email!)));

        // Signing in at acme...
        using var login = await inAcme.PasswordLoginAsync(user.Email!, password);
        Assert.True(await inAcme.IsSignedInAsync());
        var cookies = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0]));
        Assert.Contains("anubis.auth=", cookies);

        // ...gives a cookie that is a session at acme and nothing at the default tenant, even when sent there.
        Assert.Equal(HttpStatusCode.OK, await ProfileAsync(AnubisFactory.TenantHost));
        Assert.Equal(HttpStatusCode.OK, await ProfileAsync(AnubisFactory.TenantOwnHost));
        Assert.Equal(HttpStatusCode.Redirect, await ProfileAsync("localhost"));

        async Task<HttpStatusCode> ProfileAsync(string host)
        {
            using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri($"http://{host}") });
            using var request = new HttpRequestMessage(HttpMethod.Get, "/profile");
            request.Headers.Add("Cookie", cookies);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }
    }

    /// <summary>
    /// The interactive admin pages live in a Blazor circuit, after the request that chose the tenant is over:
    /// the circuit must keep working in that tenant's database.
    /// </summary>
    [Theory]
    [InlineData(null, AnubisFactory.AdminEmail, AnubisFactory.AdminPassword, AnubisFactory.TenantAdminEmail)]
    [InlineData(Acme, AnubisFactory.TenantAdminEmail, AnubisFactory.TenantAdminPassword, AnubisFactory.AdminEmail)]
    public async Task InteractivePage_KeepsWorkingInItsTenantsDatabase(string? tenant, string email, string password, string foreignEmail)
    {
        using var browser = new FormClient(_factory, tenant: tenant);
        using var login = await browser.PasswordLoginAsync(email, password);
        var cookies = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0]));

        // The users the page lists once its circuit has loaded them.
        var rendered = await BlazorCircuit.RenderAsync(_factory, browser.Http, cookies, "/users", until: email);

        Assert.Contains(email, rendered);
        Assert.DoesNotContain(foreignEmail, rendered);
    }

    // ---------------------------------------------------------------- clients and tokens

    [Fact]
    public async Task ClientsAndTokens_AreTheTenantsOwn()
    {
        using var acme = _factory.CreateAnonymousClient(AnubisFactory.TenantHost);
        using var main = _factory.CreateAnonymousClient();

        // A client of one tenant is unknown to the other.
        Assert.Null(await ClientCredentialsAsync(main, AnubisFactory.TenantServiceClientId, AnubisFactory.TenantServiceClientSecret));
        Assert.Null(await ClientCredentialsAsync(acme, AnubisFactory.ServiceClientId, AnubisFactory.ServiceClientSecret));

        var acmeToken = await ClientCredentialsAsync(acme, AnubisFactory.TenantServiceClientId, AnubisFactory.TenantServiceClientSecret);
        var mainToken = await ClientCredentialsAsync(main, AnubisFactory.ServiceClientId, AnubisFactory.ServiceClientSecret);
        Assert.NotNull(acmeToken);
        Assert.NotNull(mainToken);

        // The API of a tenant lists the tenant's users, for the tenant's token only.
        using (var users = await ApiAsync(acme, "/api/Users?count=200", acmeToken))
        {
            Assert.Equal(HttpStatusCode.OK, users.StatusCode);
            var body = await users.Content.ReadAsStringAsync();
            Assert.Contains(AnubisFactory.TenantAdminEmail, body);
            Assert.DoesNotContain(AnubisFactory.AdminEmail, body);
        }

        using (var foreign = await ApiAsync(acme, "/api/Users", mainToken))
        {
            Assert.Contains(foreign.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        }

        using (var foreign = await ApiAsync(main, "/api/Users", acmeToken))
        {
            Assert.Contains(foreign.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        }

        // Asked about another tenant's token, a tenant knows nothing.
        using (var introspection = await IntrospectAsync(main, acmeToken!, AnubisFactory.AppClientId, AnubisFactory.AppClientSecret))
        {
            Assert.False(introspection.RootElement.GetProperty("active").GetBoolean());
        }
    }

    [Fact]
    public async Task UserTokens_NameTheirTenant_AndHoldTheRolesOfItsDatabase()
    {
        using var client = new FormClient(_factory, tenant: Acme);
        var (user, password) = await client.CreateUserAsync();
        await InTenantAsync(Acme, async services =>
        {
            var roles = services.GetRequiredService<RoleAdministration>();
            var manager = (await roles.ListRolesAsync(AnubisFactory.TenantAppClientId)).Single();
            return await roles.AssignAsync(manager.Id, user.Id, null);
        });

        // The role was given in acme's database: the default tenant has neither the user nor the assignment.
        Assert.DoesNotContain(await InTenantAsync(Tenant.DefaultId, services => services.GetRequiredService<RoleAdministration>().ListAssignmentsAsync()), a => a.UserId == user.Id);

        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();
        var (idToken, accessToken, refreshToken) = await SignInAsync(client.Http, AnubisFactory.TenantAppClientId, AnubisFactory.TenantAppClientSecret, "openid roles offline_access");

        using (var payload = JsonDocument.Parse(Base64UrlDecode(idToken.Split('.')[1])))
        {
            Assert.Equal("http://acme.localhost/", payload.RootElement.GetProperty("iss").GetString());
            Assert.Equal(Acme, payload.RootElement.GetProperty("tenant").GetString());
            Assert.Equal("Manager", payload.RootElement.GetProperty("roles").GetString());   // just the name: the tenant is a claim of its own
            Assert.Equal(user.Id, payload.RootElement.GetProperty("sub").GetString());
        }

        using (var introspection = await IntrospectAsync(client.Http, accessToken, AnubisFactory.TenantAppClientId, AnubisFactory.TenantAppClientSecret))
        {
            Assert.True(introspection.RootElement.GetProperty("active").GetBoolean());
            Assert.Equal(Acme, introspection.RootElement.GetProperty("tenant").GetString());
        }

        using (var userinfo = await ApiAsync(client.Http, "/userinfo", accessToken))
        {
            Assert.Equal(HttpStatusCode.OK, userinfo.StatusCode);
            using var info = JsonDocument.Parse(await userinfo.Content.ReadAsStringAsync());
            Assert.Equal(Acme, info.RootElement.GetProperty("tenant").GetString());
        }

        // At the default tenant the access token opens nothing and the refresh token is not exchanged.
        using var main = _factory.CreateAnonymousClient();
        using (var userinfo = await ApiAsync(main, "/userinfo", accessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, userinfo.StatusCode);
        }

        foreach (var (clientId, secret) in new[] { (AnubisFactory.TenantAppClientId, AnubisFactory.TenantAppClientSecret), (AnubisFactory.AppClientId, AnubisFactory.AppClientSecret) })
        {
            using var refreshed = await main.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["refresh_token"] = refreshToken!,
            }));
            Assert.False(refreshed.IsSuccessStatusCode);
        }

        // At its own tenant it is.
        using var again = await client.Http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = AnubisFactory.TenantAppClientId,
            ["client_secret"] = AnubisFactory.TenantAppClientSecret,
            ["refresh_token"] = refreshToken!,
        }));
        Assert.True(again.IsSuccessStatusCode, await again.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public async Task Settings_AreSavedPerTenant_AndApplyToThatTenantOnly()
    {
        var file = TenantFile(Acme, "anubis.settings.json");
        try
        {
            await InTenantAsync(Acme, async services =>
            {
                var settings = services.GetRequiredService<SettingsFile>();
                Assert.Equal(file, settings.Path);
                await settings.SaveSectionAsync(GeneralOptions.SectionName, SettingsFile.ToNode(new GeneralOptions { ServerName = "Acme Sign-in" }));
                await settings.SaveSectionAsync(SecurityOptions.SectionName, SettingsFile.ToNode(new SecurityOptions { PasswordMinLength = 21 }));
                await settings.UpdateSectionAsync(AppearanceOptions.SectionName, new System.Text.Json.Nodes.JsonObject { ["Light"] = new System.Text.Json.Nodes.JsonObject { ["Primary"] = "#a1b2c3" } });
                return true;
            });

            Assert.True(File.Exists(file));
            Assert.DoesNotContain("Acme Sign-in", File.Exists(AnubisFactory.SettingsFilePath) ? await File.ReadAllTextAsync(AnubisFactory.SettingsFilePath) : string.Empty);

            using var acme = _factory.CreateAnonymousClient(AnubisFactory.TenantHost);
            using var main = _factory.CreateAnonymousClient();
            var acmeLogin = await acme.GetStringAsync("/login");
            var mainLogin = await main.GetStringAsync("/login");

            // The server name and the theme of the page...
            Assert.Contains("— Acme Sign-in</title>", acmeLogin);
            Assert.Contains("#a1b2c3", acmeLogin);
            Assert.DoesNotContain("Acme Sign-in", mainLogin);
            Assert.DoesNotContain("#a1b2c3", mainLogin);
            Assert.Contains("#123456", mainLogin);   // the server's configuration, which acme overrode for itself

            // ...and the options ASP.NET Core Identity works with, however they are asked for.
            Assert.Equal(21, await InTenantAsync(Acme, services => Task.FromResult(services.GetRequiredService<IOptions<IdentityOptions>>().Value.Password.RequiredLength)));
            Assert.Equal(21, await InTenantAsync(Acme, services => Task.FromResult(services.GetRequiredService<IOptionsSnapshot<SecurityOptions>>().Value.PasswordMinLength)));
            var mainLength = await InTenantAsync(Tenant.DefaultId, services => Task.FromResult(services.GetRequiredService<IOptionsMonitor<IdentityOptions>>().CurrentValue.Password.RequiredLength));
            Assert.NotEqual(21, mainLength);
        }
        finally
        {
            await InTenantAsync(Acme, async services =>
            {
                var settings = services.GetRequiredService<SettingsFile>();
                await settings.SaveSectionAsync(GeneralOptions.SectionName, []);
                await settings.SaveSectionAsync(SecurityOptions.SectionName, []);
                await settings.SaveSectionAsync(AppearanceOptions.SectionName, []);
                return true;
            });
        }

        using var after = _factory.CreateAnonymousClient(AnubisFactory.TenantHost);
        Assert.Contains("— Acme Ltd.</title>", await after.GetStringAsync("/login"));
    }

    // ---------------------------------------------------------------- administration

    [Fact]
    public async Task Tenant_IsCreatedWithItsDatabaseAndAdministrator_Changed_AndDeletedWithItsData()
    {
        var id = $"t-{Guid.NewGuid():N}"[..12];
        var ownHost = $"{id}.example.org";
        var administrator = new TenantAdministrator($"owner@{id}.test", "Owner-Password-123!");

        foreach (var invalid in new[] { "", "Acme", "has space", "-acme", "a.b", "a:b", new string('a', 64), Tenant.DefaultId })
        {
            Assert.Equal(TenantError.Invalid, (await Assert.ThrowsAsync<TenantAdministrationException>(() => Tenants.CreateAsync(invalid, null, null, null))).Error);
        }

        Assert.Equal(TenantError.Conflict, (await Assert.ThrowsAsync<TenantAdministrationException>(() => Tenants.CreateAsync(Acme, null, null, null))).Error);
        Assert.Equal(TenantError.Invalid, (await Assert.ThrowsAsync<TenantAdministrationException>(() => Tenants.CreateAsync(id, null, ["not a host"], null))).Error);
        Assert.Equal(TenantError.Conflict, (await Assert.ThrowsAsync<TenantAdministrationException>(() => Tenants.CreateAsync(id, null, [AnubisFactory.TenantOwnHost], null))).Error);
        Assert.Equal(TenantError.Conflict, (await Assert.ThrowsAsync<TenantAdministrationException>(() => Tenants.CreateAsync(id, null, [AnubisFactory.TenantHost], null))).Error);

        // An administrator whose password breaks the rules: no tenant is left behind.
        Assert.Equal(TenantError.Invalid, (await Assert.ThrowsAsync<TenantAdministrationException>(() => Tenants.CreateAsync(id, null, null, new TenantAdministrator(administrator.Email, "short")))).Error);
        Assert.Null(Tenants.Find(id));
        Assert.False(Directory.Exists(Path.GetDirectoryName(TenantFile(id, "anubis.db"))));

        var created = await Tenants.CreateAsync(id, "  New Tenant ", [ownHost.ToUpperInvariant(), ownHost], administrator);
        Assert.Equal("New Tenant", created.DisplayName);
        Assert.Equal([ownHost], created.Hosts);
        Assert.True(File.Exists(TenantFile(id, "anubis.db")));
        Assert.Equal(id, Catalog.Resolve($"{id}.localhost").Id);
        Assert.Equal(id, Catalog.Resolve(ownHost).Id);

        // Its database has the built-in groups, the API scope and the administrator, who can sign in at its address.
        var groups = await InTenantAsync(id, services => services.GetRequiredService<RoleManager<Group>>().Roles.Select(g => g.Name).OrderBy(n => n).ToListAsync());
        Assert.Equal([DatabaseInitializer.AdminGroup, DatabaseInitializer.UserGroup], groups);
        using (var browser = new FormClient(_factory, tenant: id))
        {
            (await browser.PasswordLoginAsync(administrator.Email, administrator.Password)).Dispose();
            Assert.True(await browser.IsSignedInAsync());
            using var users = await browser.Http.GetAsync("/users");
            Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        }

        // The id stays; the name and the host names change.
        var moved = $"sso.{ownHost}";
        var updated = await Tenants.UpdateAsync(id, "Renamed", [moved]);
        Assert.Equal(("Renamed", moved), (updated.DisplayName, updated.Hosts.Single()));
        Assert.Equal(id, Catalog.Resolve(moved).Id);
        Assert.Equal(Tenant.DefaultId, Catalog.Resolve(ownHost).Id);
        using (var browser = _factory.CreateAnonymousClient(moved))
        {
            Assert.Contains("— Renamed</title>", await browser.GetStringAsync("/login"));
        }

        Assert.Equal(TenantError.Conflict, (await Assert.ThrowsAsync<TenantAdministrationException>(() => Tenants.UpdateAsync(id, null, [AnubisFactory.TenantOwnHost]))).Error);
        Assert.Equal(TenantError.NotFound, (await Assert.ThrowsAsync<TenantAdministrationException>(() => Tenants.UpdateAsync("no-such-tenant", null, null))).Error);

        // Deleting it deletes its data; its address is the default tenant's again.
        await Tenants.DeleteAsync(id);
        Assert.Null(Tenants.Find(id));
        Assert.False(Directory.Exists(Path.GetDirectoryName(TenantFile(id, "anubis.db"))));
        Assert.Equal(Tenant.DefaultId, Catalog.Resolve($"{id}.localhost").Id);
        Assert.Equal(TenantError.NotFound, (await Assert.ThrowsAsync<TenantAdministrationException>(() => Tenants.DeleteAsync(id))).Error);
        Assert.Equal(TenantError.NotFound, (await Assert.ThrowsAsync<TenantAdministrationException>(() => Tenants.DeleteAsync(Tenant.DefaultId))).Error);

        // A new tenant of the same name starts empty.
        await Tenants.CreateAsync(id, null, null, null);
        Assert.Empty(await InTenantAsync(id, services => services.GetRequiredService<UserManager<User>>().Users.ToListAsync()));
        await Tenants.DeleteAsync(id);
    }

    [Fact]
    public async Task Scim_ManagesTenants_AtTheDefaultTenantOnly()
    {
        using var main = _factory.CreateAnonymousClient();
        main.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await ClientCredentialsAsync(main, AnubisFactory.ServiceClientId, AnubisFactory.ServiceClientSecret));

        var id = $"scim-{Guid.NewGuid():N}"[..14];
        var body = $$"""{"name":"{{id}}","displayName":"Scim Tenant","hosts":["{{id}}.example.org"],"adminEmail":"owner@{{id}}.test","adminPassword":"Owner-Password-123!"}""";
        using (var created = await main.PostAsync("/api/Tenants", Scim(body)))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("application/scim+json", created.Content.Headers.ContentType?.MediaType);
            Assert.EndsWith($"/api/Tenants/{id}", created.Headers.Location!.OriginalString);
            var json = await created.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Owner-Password", json);
            using var tenant = JsonDocument.Parse(json);
            Assert.Equal($"{id}.example.org", tenant.RootElement.GetProperty("hosts")[0].GetString());
        }

        Assert.Equal([$"owner@{id}.test"], await InTenantAsync(id, async services =>
            (await services.GetRequiredService<UserManager<User>>().GetUsersInRoleAsync(DatabaseInitializer.AdminGroup)).Select(u => u.Email).ToList()));

        using (var duplicate = await main.PostAsync("/api/Tenants", Scim($$"""{"name":"{{id}}"}""")))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        foreach (var invalid in new[] { """{"name":"Not Valid"}""", """{"name":"half","adminEmail":"a@b.test"}""", """{"name":"default"}""" })
        {
            using var refused = await main.PostAsync("/api/Tenants", Scim(invalid));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        using (var patched = await main.PatchAsync($"/api/Tenants/{id}", Scim($$"""{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchRequest"],"Operations":[{"op":"replace","path":"displayName","value":"Renamed"},{"op":"add","value":{"hosts":["sso.{{id}}.example.org"]} } ]}""")))
        {
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        }

        Assert.Equal(id, Catalog.Resolve($"sso.{id}.example.org").Id);

        using (var list = await main.GetFromJsonAsync<JsonDocument>("/api/Tenants?sortBy=name&count=200"))
        {
            var names = list!.RootElement.GetProperty("Resources").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            Assert.Contains(Tenant.DefaultId, names);
            Assert.Contains(Acme, names);
            Assert.Contains(id, names);
        }

        using (var one = await main.GetFromJsonAsync<JsonDocument>($"/api/Tenants?filter=name eq \"{id}\""))
        {
            Assert.Equal(1, one!.RootElement.GetProperty("totalResults").GetInt32());
            Assert.Equal("Renamed", one.RootElement.GetProperty("Resources")[0].GetProperty("displayName").GetString());
        }

        using (var renamed = await main.PutAsync($"/api/Tenants/{id}", Scim("""{"name":"another"}""")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);   // the name is the identity
        }

        using (var replaced = await main.PutAsync($"/api/Tenants/{id}", Scim("""{"displayName":"Replaced"}""")))
        {
            Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
            Assert.Empty(Tenants.Find(id)!.Hosts);   // PUT replaces everything
        }

        using (var fixedTenant = await main.PutAsync($"/api/Tenants/{Tenant.DefaultId}", Scim("""{"displayName":"Mine"}""")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, fixedTenant.StatusCode);
        }

        // The new tenant's own API knows no tenants: they are the default tenant's to manage.
        using (var acme = _factory.CreateAnonymousClient(AnubisFactory.TenantHost))
        {
            using var response = await ApiAsync(acme, "/api/Tenants", await ClientCredentialsAsync(acme, AnubisFactory.TenantServiceClientId, AnubisFactory.TenantServiceClientSecret));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var deleted = await main.DeleteAsync($"/api/Tenants/{id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var gone = await main.GetAsync($"/api/Tenants/{id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        using (var never = await main.DeleteAsync($"/api/Tenants/{Tenant.DefaultId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, never.StatusCode);
        }
    }

    [Fact]
    public async Task TenantsPage_IsTheDefaultTenantsOnly()
    {
        using var main = new FormClient(_factory);
        (await main.PasswordLoginAsync(AnubisFactory.AdminEmail, AnubisFactory.AdminPassword)).Dispose();
        var page = await main.Http.GetStringAsync("/tenants");
        Assert.Contains("<code>acme</code>", page);
        Assert.Contains("<code>default</code>", page);
        Assert.Contains($"href=\"http://{AnubisFactory.TenantOwnHost}/\"", page);   // where the tenant is: its own host name
        Assert.Contains($"href=\"http://{AnubisFactory.TenantHost}/\"", page);      // and its subdomain

        // The tenant's administrator runs the tenant (users, clients, settings), not the tenants.
        using var acme = new FormClient(_factory, tenant: Acme);
        (await acme.PasswordLoginAsync(AnubisFactory.TenantAdminEmail, AnubisFactory.TenantAdminPassword)).Dispose();
        var users = await acme.Http.GetStringAsync("/users");
        Assert.Contains(AnubisFactory.TenantAdminEmail, users);
        Assert.DoesNotContain(AnubisFactory.AdminEmail, users);
        Assert.Contains("href=\"/options-general\"", users);
        Assert.DoesNotContain("href=\"/tenants\"", users);

        var hidden = await acme.Http.GetStringAsync("/tenants");
        Assert.DoesNotContain("<code>acme</code>", hidden);
        Assert.Contains(k6labs.Anubis.Resources.Translations.Routes_NotFoundTitle, hidden);
    }

    // ---------------------------------------------------------------- helpers

    private static StringContent Scim(string json) => new(json, Encoding.UTF8, "application/scim+json");

    /// <summary>The access token of a client credentials grant; null when the client is refused.</summary>
    private static async Task<string?> ClientCredentialsAsync(HttpClient http, string clientId, string secret)
    {
        using var response = await http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = "anubis-api",
        }));
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("access_token").GetString();
    }

    private static async Task<HttpResponseMessage> ApiAsync(HttpClient http, string url, string? accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await http.SendAsync(request);
    }

    private static async Task<JsonDocument> IntrospectAsync(HttpClient http, string token, string clientId, string secret)
    {
        using var response = await http.PostAsync("/introspect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
            ["client_id"] = clientId,
            ["client_secret"] = secret,
        }));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body);
    }

    /// <summary>The authorization code flow of a signed-in browser: the ID, access and refresh token.</summary>
    private static async Task<(string IdToken, string AccessToken, string? RefreshToken)> SignInAsync(HttpClient http, string clientId, string secret, string scope)
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var url = "/authorize?" + string.Join('&',
            $"client_id={clientId}",
            $"redirect_uri={Uri.EscapeDataString(AnubisFactory.AppClientRedirectUri)}",
            "response_type=code", $"scope={Uri.EscapeDataString(scope)}", "state=s", "nonce=n",
            $"code_challenge={challenge}", "code_challenge_method=S256");

        using var authorize = await http.GetAsync(url);
        Assert.StartsWith(AnubisFactory.AppClientRedirectUri, authorize.Headers.Location!.OriginalString);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location.Query)["code"].ToString();

        using var token = await http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["code"] = code,
            ["redirect_uri"] = AnubisFactory.AppClientRedirectUri,
            ["code_verifier"] = verifier,
        }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return (json.RootElement.GetProperty("id_token").GetString()!, json.RootElement.GetProperty("access_token").GetString()!,
            json.RootElement.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
