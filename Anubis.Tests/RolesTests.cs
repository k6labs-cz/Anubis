using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Anubis.Tests.Support;

using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Services;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

/// <summary>
/// Tenants and the roles applications define: seeded from configuration, given to users and groups,
/// issued in the "roles" claim of a token for the application, taken back at once, and managed over SCIM.
/// </summary>
[Collection(AnubisCollection.Name)]
public class RolesTests
{
    private readonly AnubisFactory _factory;

    public RolesTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    private async Task<T> WithRolesAsync<T>(Func<RoleAdministration, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<RoleAdministration>());
    }

    private Task<RoleSummary> AppRoleAsync(string name)
        => WithRolesAsync(async roles => (await roles.ListRolesAsync(AnubisFactory.AppClientId)).Single(r => r.Name == name));

    [Fact]
    public async Task Configuration_SeedsTheTenantAndTheApplicationsRoles()
    {
        var tenants = await WithRolesAsync(roles => roles.ListTenantsAsync());
        Assert.Equal("Acme Ltd.", tenants.Single(t => t.Id == AnubisFactory.SeededTenant).DisplayName);

        var roleNames = (await WithRolesAsync(roles => roles.ListRolesAsync(AnubisFactory.AppClientId))).Select(r => r.Name);
        Assert.Equal(["Administrator", "User"], roleNames);
    }

    [Fact]
    public async Task Names_AreValidated_AndAnAssignmentIsForAUserOrAGroup()
    {
        await WithRolesAsync(async roles =>
        {
            foreach (var id in new[] { "", "Acme", "has space", "-acme", "a:b", new string('a', 64) })
            {
                Assert.Equal(RoleError.Invalid, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.CreateTenantAsync(id, null))).Error);
            }

            Assert.Equal(RoleError.Conflict, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.CreateTenantAsync(AnubisFactory.SeededTenant, null))).Error);

            // The colon separates the tenant from the role in the claim.
            Assert.Equal(RoleError.Invalid, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.CreateRoleAsync(AnubisFactory.AppClientId, "a:b", null))).Error);
            Assert.Equal(RoleError.Invalid, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.CreateRoleAsync("no-such-client", "Reader", null))).Error);
            Assert.Equal(RoleError.Conflict, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.CreateRoleAsync(AnubisFactory.AppClientId, "User", null))).Error);

            var role = (await roles.ListRolesAsync(AnubisFactory.AppClientId)).First();
            Assert.Equal(RoleError.Invalid, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.AssignAsync(role.Id, null, null, null))).Error);
            Assert.Equal(RoleError.Invalid, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.AssignAsync(role.Id, "u", "g", null))).Error);
            Assert.Equal(RoleError.NotFound, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.AssignAsync(role.Id, "no-such-user", null, null))).Error);
            return true;
        });
    }

    [Fact]
    public async Task RolesScope_PutsTheUsersAndTheirGroupsRolesIntoATokenForTheApplication_AndRemovalRevokesIt()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        var tenant = $"t-{Guid.NewGuid():N}"[..12];
        var groupName = $"roles-{Guid.NewGuid():N}"[..20];
        string groupId;
        using (var scope = _factory.Services.CreateScope())
        {
            var groups = scope.ServiceProvider.GetRequiredService<RoleManager<Group>>();
            Assert.True((await groups.CreateAsync(new Group(groupName))).Succeeded);
            groupId = (await groups.FindByNameAsync(groupName))!.Id;
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(user.Id))!, groupName)).Succeeded);
        }

        var userRole = await AppRoleAsync("User");
        var adminRole = await AppRoleAsync("Administrator");
        var direct = await WithRolesAsync(async roles =>
        {
            await roles.CreateTenantAsync(tenant, null);
            await roles.AssignAsync(adminRole.Id, null, groupId, AnubisFactory.SeededTenant);   // through the group, in one tenant
            await roles.AssignAsync(userRole.Id, null, groupId, null);                           // through the group, everywhere
            return await roles.AssignAsync(userRole.Id, user.Id, null, tenant);                  // the user's own
        });
        Assert.Equal($"{tenant}:User", direct.Claim);

        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();
        string[] expected = ["User", $"{AnubisFactory.SeededTenant}:Administrator", $"{tenant}:User"];

        var (idToken, accessToken) = await SignInAsync(client, "openid roles");
        using (var payload = JsonDocument.Parse(Base64UrlDecode(idToken.Split('.')[1])))
        {
            Assert.Equal(expected.Order(StringComparer.Ordinal), Strings(payload.RootElement, "roles"));
        }

        using (var introspection = await IntrospectAsync(client.Http, accessToken))
        {
            Assert.True(introspection.RootElement.GetProperty("active").GetBoolean());
            Assert.Contains(AnubisFactory.AppClientId, Strings(introspection.RootElement, "aud"));   // issued for the application
            Assert.Equal(expected.Order(StringComparer.Ordinal), Strings(introspection.RootElement, "roles"));
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/userinfo"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var userinfo = await client.Http.SendAsync(request);
            using var info = JsonDocument.Parse(await userinfo.Content.ReadAsStringAsync());
            Assert.Equal(expected.Order(StringComparer.Ordinal), Strings(info.RootElement, "roles"));
        }

        // Without the scope the token says nothing about roles and isn't for the application.
        var (plainId, plainAccess) = await SignInAsync(client, "openid");
        using (var payload = JsonDocument.Parse(Base64UrlDecode(plainId.Split('.')[1])))
        {
            Assert.False(payload.RootElement.TryGetProperty("roles", out _));
        }

        // Taking the role away ends the tokens it is written in; the next sign-in is without it.
        await WithRolesAsync(async roles => { await roles.RemoveAssignmentAsync(direct.Id); return true; });
        using (var introspection = await IntrospectAsync(client.Http, accessToken))
        {
            Assert.False(introspection.RootElement.GetProperty("active").GetBoolean());
        }

        var (laterId, _) = await SignInAsync(client, "openid roles");
        using (var payload = JsonDocument.Parse(Base64UrlDecode(laterId.Split('.')[1])))
        {
            Assert.DoesNotContain($"{tenant}:User", Strings(payload.RootElement, "roles"));
        }

        // Deleting a tenant takes its assignments with it.
        await WithRolesAsync(async roles => { await roles.DeleteTenantAsync(tenant); return true; });
        Assert.DoesNotContain(await WithRolesAsync(roles => roles.ListAssignmentsAsync()), a => a.TenantId == tenant);
        Assert.NotNull(plainAccess);
    }

    [Fact]
    public async Task Scim_ManagesTenantsRolesAndAssignments()
    {
        using var anonymous = _factory.CreateAnonymousClient();
        using var tokenResponse = await anonymous.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AnubisFactory.ServiceClientId,
            ["client_secret"] = AnubisFactory.ServiceClientSecret,
            ["scope"] = "anubis-api",
        }));
        using var tokenPayload = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        using var api = _factory.CreateAnonymousClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenPayload.RootElement.GetProperty("access_token").GetString());

        // --- tenant
        var tenant = $"scim-{Guid.NewGuid():N}"[..14];
        using (var created = await api.PostAsync("/api/Tenants", Scim($$"""{"name":"{{tenant}}","displayName":"Scim Tenant"}""")))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("application/scim+json", created.Content.Headers.ContentType?.MediaType);
            Assert.EndsWith($"/api/Tenants/{tenant}", created.Headers.Location!.OriginalString);
        }

        using (var duplicate = await api.PostAsync("/api/Tenants", Scim($$"""{"name":"{{tenant}}"}""")))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        using (var invalid = await api.PostAsync("/api/Tenants", Scim("""{"name":"Not Valid"}""")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        using (var patched = await api.PatchAsync($"/api/Tenants/{tenant}", Scim("""{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchRequest"],"Operations":[{"op":"replace","path":"displayName","value":"Renamed"}]}""")))
        {
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        }

        using (var list = await api.GetFromJsonAsync<JsonDocument>($"/api/Tenants?filter=name eq \"{tenant}\""))
        {
            Assert.Equal(1, list!.RootElement.GetProperty("totalResults").GetInt32());
            Assert.Equal("Renamed", list.RootElement.GetProperty("Resources")[0].GetProperty("displayName").GetString());
        }

        using (var renamed = await api.PutAsync($"/api/Tenants/{tenant}", Scim("""{"name":"another"}""")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);   // the name is the identity
        }

        // --- role
        var roleName = $"Reader{Guid.NewGuid():N}"[..14];
        string roleId;
        using (var created = await api.PostAsync("/api/Roles", Scim($$"""{"client":"{{AnubisFactory.AppClientId}}","name":"{{roleName}}","description":"Reads"}""")))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var role = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            roleId = role.RootElement.GetProperty("id").GetString()!;
        }

        using (var duplicate = await api.PostAsync("/api/Roles", Scim($$"""{"client":"{{AnubisFactory.AppClientId}}","name":"{{roleName}}"}""")))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        using (var roles = await api.GetFromJsonAsync<JsonDocument>($"/api/Roles?filter=client eq \"{AnubisFactory.AppClientId}\"&sortBy=name"))
        {
            Assert.Contains(roleName, roles!.RootElement.GetProperty("Resources").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        }

        // --- assignment: the role by client + name, a user, a tenant
        using var forms = new FormClient(_factory);
        var (user, _) = await forms.CreateUserAsync();
        string assignmentId;
        var body = $$"""{"role":{"client":"{{AnubisFactory.AppClientId}}","name":"{{roleName}}"},"user":{"value":"{{user.Id}}"},"tenant":"{{tenant}}"}""";
        using (var created = await api.PostAsync("/api/RoleAssignments", Scim(body)))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var assignment = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            assignmentId = assignment.RootElement.GetProperty("id").GetString()!;
            Assert.Equal($"{tenant}:{roleName}", assignment.RootElement.GetProperty("claim").GetString());
            Assert.Equal(roleId, assignment.RootElement.GetProperty("role").GetProperty("value").GetString());
            Assert.Equal(user.Email, assignment.RootElement.GetProperty("user").GetProperty("display").GetString());
        }

        using (var duplicate = await api.PostAsync("/api/RoleAssignments", Scim(body)))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        using (var nobody = await api.PostAsync("/api/RoleAssignments", Scim($$"""{"role":{"value":"{{roleId}}"} }""")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, nobody.StatusCode);
        }

        using (var list = await api.GetFromJsonAsync<JsonDocument>($"/api/RoleAssignments?filter=tenant eq \"{tenant}\""))
        {
            Assert.Equal(assignmentId, list!.RootElement.GetProperty("Resources")[0].GetProperty("id").GetString());
        }

        // --- deleting the tenant takes the assignment along; the role goes last
        using (var deleted = await api.DeleteAsync($"/api/Tenants/{tenant}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var gone = await api.GetAsync($"/api/RoleAssignments/{assignmentId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        using (var deleted = await api.DeleteAsync($"/api/Roles/{roleId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var again = await api.DeleteAsync($"/api/Roles/{roleId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static StringContent Scim(string json) => new(json, Encoding.UTF8, "application/scim+json");

    private static List<string> Strings(JsonElement root, string name)
        => !root.TryGetProperty(name, out var value) ? []
            : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(v => v.GetString()!).Order(StringComparer.Ordinal).ToList()
            : [value.GetString()!];

    /// <summary>The authorization code flow of the application with roles (no consent page): its ID and access token.</summary>
    private static async Task<(string IdToken, string AccessToken)> SignInAsync(FormClient client, string scope)
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var url = "/authorize?" + string.Join('&',
            $"client_id={AnubisFactory.AppClientId}",
            $"redirect_uri={Uri.EscapeDataString(AnubisFactory.AppClientRedirectUri)}",
            "response_type=code", $"scope={Uri.EscapeDataString(scope)}", "state=s", "nonce=n",
            $"code_challenge={challenge}", "code_challenge_method=S256");

        using var authorize = await client.Http.GetAsync(url);
        Assert.StartsWith(AnubisFactory.AppClientRedirectUri, authorize.Headers.Location!.OriginalString);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location.Query)["code"].ToString();

        using var token = await client.Http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = AnubisFactory.AppClientId,
            ["client_secret"] = AnubisFactory.AppClientSecret,
            ["code"] = code,
            ["redirect_uri"] = AnubisFactory.AppClientRedirectUri,
            ["code_verifier"] = verifier,
        }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return (json.RootElement.GetProperty("id_token").GetString()!, json.RootElement.GetProperty("access_token").GetString()!);
    }

    /// <summary>What the application learns about its token from Anubis.</summary>
    private static async Task<JsonDocument> IntrospectAsync(HttpClient http, string accessToken)
    {
        using var response = await http.PostAsync("/introspect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = accessToken,
            ["client_id"] = AnubisFactory.AppClientId,
            ["client_secret"] = AnubisFactory.AppClientSecret,
        }));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
