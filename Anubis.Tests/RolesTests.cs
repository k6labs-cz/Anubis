using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Anubis.Tests.Support;

using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Services;
using k6labs.Anubis.Tenancy;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

/// <summary>
/// The roles applications define: seeded from configuration, given to users and groups, issued in the
/// "roles" claim of a token for the application (next to the "tenant" claim), taken back at once, and
/// managed over SCIM.
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
    public async Task Configuration_SeedsTheApplicationsRoles()
    {
        var roleNames = (await WithRolesAsync(roles => roles.ListRolesAsync(AnubisFactory.AppClientId))).Select(r => r.Name);
        Assert.Equal(["Administrator", "User"], roleNames);
    }

    [Fact]
    public async Task Names_AreValidated_AndAnAssignmentIsForAUserOrAGroup()
    {
        await WithRolesAsync(async roles =>
        {
            Assert.Equal(RoleError.Invalid, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.CreateRoleAsync(AnubisFactory.AppClientId, "a:b", null))).Error);
            Assert.Equal(RoleError.Invalid, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.CreateRoleAsync(AnubisFactory.AppClientId, "has space", null))).Error);
            Assert.Equal(RoleError.Invalid, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.CreateRoleAsync("no-such-client", "Reader", null))).Error);
            Assert.Equal(RoleError.Conflict, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.CreateRoleAsync(AnubisFactory.AppClientId, "User", null))).Error);

            var role = (await roles.ListRolesAsync(AnubisFactory.AppClientId)).First();
            Assert.Equal(RoleError.Invalid, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.AssignAsync(role.Id, null, null))).Error);
            Assert.Equal(RoleError.Invalid, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.AssignAsync(role.Id, "u", "g"))).Error);
            Assert.Equal(RoleError.NotFound, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.AssignAsync(role.Id, "no-such-user", null))).Error);
            return true;
        });
    }

    [Fact]
    public async Task RolesScope_PutsTheUsersAndTheirGroupsRolesIntoATokenForTheApplication_AndRemovalRevokesIt()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
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
            await roles.AssignAsync(userRole.Id, null, groupId);          // through the group
            Assert.Equal(RoleError.Conflict, (await Assert.ThrowsAsync<RoleAdministrationException>(() => roles.AssignAsync(userRole.Id, null, groupId))).Error);
            await roles.AssignAsync(userRole.Id, user.Id, null);          // and again as the user's own: once in the claim
            return await roles.AssignAsync(adminRole.Id, user.Id, null);  // the user's own
        });
        Assert.Equal("Administrator", direct.Claim);

        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();
        string[] expected = ["Administrator", "User"];

        var (idToken, accessToken) = await SignInAsync(client, "openid roles");
        using (var payload = JsonDocument.Parse(Base64UrlDecode(idToken.Split('.')[1])))
        {
            Assert.Equal(expected, Strings(payload.RootElement, "roles"));
            Assert.Equal(Tenant.DefaultId, payload.RootElement.GetProperty("tenant").GetString());   // the tenant the roles hold in
        }

        using (var introspection = await IntrospectAsync(client.Http, accessToken))
        {
            Assert.True(introspection.RootElement.GetProperty("active").GetBoolean());
            Assert.Contains(AnubisFactory.AppClientId, Strings(introspection.RootElement, "aud"));   // issued for the application
            Assert.Equal(expected, Strings(introspection.RootElement, "roles"));
            Assert.Equal(Tenant.DefaultId, introspection.RootElement.GetProperty("tenant").GetString());
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/userinfo"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var userinfo = await client.Http.SendAsync(request);
            using var info = JsonDocument.Parse(await userinfo.Content.ReadAsStringAsync());
            Assert.Equal(expected, Strings(info.RootElement, "roles"));
            Assert.Equal(Tenant.DefaultId, info.RootElement.GetProperty("tenant").GetString());
        }

        // Without the scope the token says nothing about roles; the tenant is always there.
        var (plainId, _) = await SignInAsync(client, "openid");
        using (var payload = JsonDocument.Parse(Base64UrlDecode(plainId.Split('.')[1])))
        {
            Assert.False(payload.RootElement.TryGetProperty("roles", out _));
            Assert.Equal(Tenant.DefaultId, payload.RootElement.GetProperty("tenant").GetString());
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
            Assert.Equal(["User"], Strings(payload.RootElement, "roles"));
        }
    }

    [Fact]
    public async Task Scim_ManagesRolesAndAssignments()
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

        // --- assignment: the role by client + name, a user
        using var forms = new FormClient(_factory);
        var (user, _) = await forms.CreateUserAsync();
        string assignmentId;
        var body = $$"""{"role":{"client":"{{AnubisFactory.AppClientId}}","name":"{{roleName}}"},"user":{"value":"{{user.Id}}"} }""";
        using (var created = await api.PostAsync("/api/RoleAssignments", Scim(body)))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var assignment = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            assignmentId = assignment.RootElement.GetProperty("id").GetString()!;
            Assert.Equal(roleName, assignment.RootElement.GetProperty("claim").GetString());
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

        using (var list = await api.GetFromJsonAsync<JsonDocument>($"/api/RoleAssignments?filter=claim eq \"{roleName}\""))
        {
            Assert.Equal(assignmentId, list!.RootElement.GetProperty("Resources")[0].GetProperty("id").GetString());
        }

        // --- deleting the role takes the assignment along
        using (var deleted = await api.DeleteAsync($"/api/Roles/{roleId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var gone = await api.GetAsync($"/api/RoleAssignments/{assignmentId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
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
