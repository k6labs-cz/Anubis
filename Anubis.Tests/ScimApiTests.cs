using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Anubis.Tests;

/// <summary>
/// The SCIM 2.0 provisioning API under /api/ (RFC 7643 / 7644): bearer token from /token with the
/// "anubis-api" scope manages users, groups and custom claims; errors follow RFC 7644.
/// </summary>
[Collection(AnubisCollection.Name)]
public class ScimApiTests
{
    private readonly AnubisFactory _factory;

    public ScimApiTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    private async Task<string> ServiceTokenAsync(HttpClient client, string scope = "anubis-api")
    {
        using var response = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AnubisFactory.ServiceClientId,
            ["client_secret"] = AnubisFactory.ServiceClientSecret,
            ["scope"] = scope,
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("access_token").GetString()!;
    }

    private HttpClient ApiClient(string token)
    {
        var client = _factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Scim(string json) => new(json, Encoding.UTF8, "application/scim+json");

    private static void AssertScimContentType(HttpResponseMessage response)
        => Assert.Equal("application/scim+json", response.Content.Headers.ContentType?.MediaType);

    [Fact]
    public async Task Users_WithoutToken_IsUnauthorized()
    {
        using var client = _factory.CreateAnonymousClient();

        using var response = await client.GetAsync("/api/Users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Users_WithTokenWithoutApiScope_IsForbidden()
    {
        using var anonymous = _factory.CreateAnonymousClient();
        var token = await ServiceTokenAsync(anonymous, scope: "openid");
        using var client = ApiClient(token);

        using var response = await client.GetAsync("/api/Users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ServiceProviderConfig_DescribesTheApi()
    {
        using var anonymous = _factory.CreateAnonymousClient();
        using var client = ApiClient(await ServiceTokenAsync(anonymous));

        using var response = await client.GetAsync("/api/ServiceProviderConfig");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertScimContentType(response);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("urn:ietf:params:scim:api:messages:2.0:ServiceProviderConfig", payload.RootElement.GetProperty("schemas").EnumerateArray().Select(e => e.GetString()));
        Assert.True(payload.RootElement.GetProperty("patch").GetProperty("supported").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("bulk").GetProperty("supported").GetBoolean());
        Assert.Contains("anubis-api", payload.RootElement.GetProperty("authenticationSchemes")[0].GetProperty("description").GetString());
    }

    [Fact]
    public async Task ResourceTypes_And_Schemas_ArePublished()
    {
        using var anonymous = _factory.CreateAnonymousClient();
        using var client = ApiClient(await ServiceTokenAsync(anonymous));

        using var resourceTypes = await client.GetFromJsonAsync<JsonDocument>("/api/ResourceTypes");
        Assert.NotNull(resourceTypes);
        var endpoints = resourceTypes.RootElement.GetProperty("Resources").EnumerateArray().Select(r => r.GetProperty("endpoint").GetString()).ToList();
        Assert.Contains("/api/Users", endpoints);
        Assert.Contains("/api/Groups", endpoints);
        Assert.Contains("/api/Tenants", endpoints);
        Assert.Contains("/api/Roles", endpoints);
        Assert.Contains("/api/RoleAssignments", endpoints);

        using var schemas = await client.GetFromJsonAsync<JsonDocument>("/api/Schemas");
        Assert.NotNull(schemas);
        var ids = schemas.RootElement.GetProperty("Resources").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Contains("urn:ietf:params:scim:schemas:core:2.0:User", ids);
        Assert.Contains("urn:ietf:params:scim:schemas:core:2.0:Group", ids);

        using var response = await client.GetAsync("/api/Schemas/urn:ietf:params:scim:schemas:core:2.0:User");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task User_FullLifecycle_CreateGetListPatchPutDelete()
    {
        using var anonymous = _factory.CreateAnonymousClient();
        using var client = ApiClient(await ServiceTokenAsync(anonymous));
        var email = $"scim-{Guid.NewGuid():N}@test.local";

        // POST /api/Users
        using var createResponse = await client.PostAsync("/api/Users", Scim($$"""
        {
            "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
            "userName": "{{email}}",
            "displayName": "Scim User",
            "active": true,
            "password": "Scim-Password-123!",
            "emails": [{ "value": "{{email}}", "type": "work", "primary": true }],
            "urn:ietf:params:scim:schemas:extension:anubis:2.0:User": {
                "claims": [{ "type": "department", "value": "sales" }],
                "emailConfirmed": true
            }
        }
        """));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        AssertScimContentType(createResponse);
        Assert.NotNull(createResponse.Headers.Location);
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetString()!;
        Assert.Equal(email, created.RootElement.GetProperty("userName").GetString());
        Assert.Equal("Scim User", created.RootElement.GetProperty("displayName").GetString());
        Assert.True(created.RootElement.GetProperty("active").GetBoolean());
        Assert.Equal("sales", created.RootElement.GetProperty("urn:ietf:params:scim:schemas:extension:anubis:2.0:User").GetProperty("claims")[0].GetProperty("value").GetString());
        Assert.DoesNotContain("password", created.RootElement.ToString());
        var userGroup = created.RootElement.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("display").GetString() == "Users");
        var userGroupId = userGroup.GetProperty("value").GetString()!;

        // GET /api/Users/{id}
        using var getResponse = await client.GetAsync($"/api/Users/{id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using var fetched = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        Assert.Equal(id, fetched.RootElement.GetProperty("id").GetString());
        Assert.Equal("User", fetched.RootElement.GetProperty("meta").GetProperty("resourceType").GetString());

        // GET /api/Users?filter=...
        using var listResponse = await client.GetFromJsonAsync<JsonDocument>($"/api/Users?filter={Uri.EscapeDataString($"userName eq \"{email}\"")}");
        Assert.NotNull(listResponse);
        Assert.Equal(1, listResponse.RootElement.GetProperty("totalResults").GetInt32());
        Assert.Equal(email, listResponse.RootElement.GetProperty("Resources")[0].GetProperty("userName").GetString());

        // PATCH: rename, deactivate, add a claim
        using var patchResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/Users/{id}")
        {
            Content = Scim($$"""
            {
                "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchRequest"],
                "Operations": [
                    { "op": "replace", "path": "displayName", "value": "Renamed User" },
                    { "op": "replace", "path": "active", "value": false },
                    { "op": "add", "path": "urn:ietf:params:scim:schemas:extension:anubis:2.0:User:claims", "value": { "type": "level", "value": "gold" } }
                ]
            }
            """),
        });
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        using var patched = JsonDocument.Parse(await patchResponse.Content.ReadAsStringAsync());
        Assert.Equal("Renamed User", patched.RootElement.GetProperty("displayName").GetString());
        Assert.False(patched.RootElement.GetProperty("active").GetBoolean());
        var claims = patched.RootElement.GetProperty("urn:ietf:params:scim:schemas:extension:anubis:2.0:User").GetProperty("claims").EnumerateArray()
            .Select(c => c.GetProperty("type").GetString()).ToList();
        Assert.Contains("department", claims);
        Assert.Contains("level", claims);

        // PATCH: remove the group, reactivate
        using var patch2 = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/Users/{id}")
        {
            Content = Scim($$"""
            {
                "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchRequest"],
                "Operations": [
                    { "op": "remove", "path": "groups[value eq \"{{userGroupId}}\"]" },
                    { "op": "replace", "path": "active", "value": true }
                ]
            }
            """),
        });
        Assert.Equal(HttpStatusCode.OK, patch2.StatusCode);
        using var patched2 = JsonDocument.Parse(await patch2.Content.ReadAsStringAsync());
        Assert.Empty(patched2.RootElement.GetProperty("groups").EnumerateArray());
        Assert.True(patched2.RootElement.GetProperty("active").GetBoolean());

        // PUT: full replace (displayName only, no groups, no claims extension)
        using var putResponse = await client.PutAsync($"/api/Users/{id}", Scim($$"""
        {
            "userName": "{{email}}",
            "displayName": "Put User"
        }
        """));
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        using var replaced = JsonDocument.Parse(await putResponse.Content.ReadAsStringAsync());
        Assert.Equal("Put User", replaced.RootElement.GetProperty("displayName").GetString());
        Assert.Empty(replaced.RootElement.GetProperty("urn:ietf:params:scim:schemas:extension:anubis:2.0:User").GetProperty("claims").EnumerateArray());

        // DELETE
        using var deleteResponse = await client.DeleteAsync($"/api/Users/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var goneResponse = await client.GetAsync($"/api/Users/{id}");
        Assert.Equal(HttpStatusCode.NotFound, goneResponse.StatusCode);
        using var error = JsonDocument.Parse(await goneResponse.Content.ReadAsStringAsync());
        Assert.Contains("urn:ietf:params:scim:api:messages:2.0:Error", error.RootElement.GetProperty("schemas").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("404", error.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateUser_DuplicateUserName_Is409Uniqueness()
    {
        using var anonymous = _factory.CreateAnonymousClient();
        using var client = ApiClient(await ServiceTokenAsync(anonymous));
        var email = $"scim-dup-{Guid.NewGuid():N}@test.local";
        var body = $$"""{ "userName": "{{email}}" }""";

        using var first = await client.PostAsync("/api/Users", Scim(body));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await client.PostAsync("/api/Users", Scim(body));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var error = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal("uniqueness", error.RootElement.GetProperty("scimType").GetString());
        Assert.Equal("409", error.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateUser_InvalidEmail_OrReservedClaim_Is400()
    {
        using var anonymous = _factory.CreateAnonymousClient();
        using var client = ApiClient(await ServiceTokenAsync(anonymous));

        using var badEmail = await client.PostAsync("/api/Users", Scim("""{ "userName": "not-an-email" }"""));
        Assert.Equal(HttpStatusCode.BadRequest, badEmail.StatusCode);

        var email = $"scim-res-{Guid.NewGuid():N}@test.local";
        using var reserved = await client.PostAsync("/api/Users", Scim($$"""
        {
            "userName": "{{email}}",
            "urn:ietf:params:scim:schemas:extension:anubis:2.0:User": { "claims": [{ "type": "sub", "value": "fake" }] }
        }
        """));
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
        using var error = JsonDocument.Parse(await reserved.Content.ReadAsStringAsync());
        Assert.Equal("invalidValue", error.RootElement.GetProperty("scimType").GetString());
    }

    [Fact]
    public async Task Patch_UnsupportedPath_Is400InvalidPath()
    {
        using var anonymous = _factory.CreateAnonymousClient();
        using var client = ApiClient(await ServiceTokenAsync(anonymous));

        using var create = await client.PostAsync("/api/Users", Scim($$"""{ "userName": "scim-path-{{Guid.NewGuid():N}}@test.local" }"""));
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetString()!;

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/Users/{id}")
        {
            Content = Scim("""
            {
                "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchRequest"],
                "Operations": [{ "op": "replace", "path": "nickName", "value": "x" }]
            }
            """),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalidPath", error.RootElement.GetProperty("scimType").GetString());
    }

    [Fact]
    public async Task List_InvalidFilter_Is400InvalidFilter_AndPagingWorks()
    {
        using var anonymous = _factory.CreateAnonymousClient();
        using var client = ApiClient(await ServiceTokenAsync(anonymous));

        using var bad = await client.GetAsync("/api/Users?filter=this is not a filter");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var error = JsonDocument.Parse(await bad.Content.ReadAsStringAsync());
        Assert.Equal("invalidFilter", error.RootElement.GetProperty("scimType").GetString());

        using var page = await client.GetFromJsonAsync<JsonDocument>("/api/Users?count=1&startIndex=2&sortBy=userName");
        Assert.NotNull(page);
        Assert.Equal(1, page.RootElement.GetProperty("itemsPerPage").GetInt32());
        Assert.Equal(2, page.RootElement.GetProperty("startIndex").GetInt32());
        Assert.True(page.RootElement.GetProperty("totalResults").GetInt32() >= 2);
    }

    [Fact]
    public async Task Groups_FullLifecycle_AndBuiltInGroupIsProtected()
    {
        using var anonymous = _factory.CreateAnonymousClient();
        using var client = ApiClient(await ServiceTokenAsync(anonymous));
        var groupName = $"scim-group-{Guid.NewGuid():N}";

        // A user to add as a member.
        using var createUser = await client.PostAsync("/api/Users", Scim($$"""{ "userName": "scim-member-{{Guid.NewGuid():N}}@test.local" }"""));
        using var user = JsonDocument.Parse(await createUser.Content.ReadAsStringAsync());
        var userId = user.RootElement.GetProperty("id").GetString()!;

        // POST /api/Groups
        using var createResponse = await client.PostAsync("/api/Groups", Scim($$"""
        { "schemas": ["urn:ietf:params:scim:schemas:core:2.0:Group"], "displayName": "{{groupName}}" }
        """));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var groupId = created.RootElement.GetProperty("id").GetString()!;
        Assert.Equal(groupName, created.RootElement.GetProperty("displayName").GetString());

        // PATCH: add a member
        using var addMember = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/Groups/{groupId}")
        {
            Content = Scim($$"""
            {
                "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchRequest"],
                "Operations": [{ "op": "add", "path": "members", "value": [{ "value": "{{userId}}" }] }]
            }
            """),
        });
        Assert.Equal(HttpStatusCode.OK, addMember.StatusCode);
        using var withMember = JsonDocument.Parse(await addMember.Content.ReadAsStringAsync());
        Assert.Equal(userId, Assert.Single(withMember.RootElement.GetProperty("members").EnumerateArray()).GetProperty("value").GetString());

        // The user sees the group.
        using var userAfter = await client.GetFromJsonAsync<JsonDocument>($"/api/Users/{userId}");
        Assert.NotNull(userAfter);
        Assert.Contains(groupId, userAfter.RootElement.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("value").GetString()));

        // PATCH: remove the member
        using var removeMember = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/Groups/{groupId}")
        {
            Content = Scim($$"""
            {
                "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchRequest"],
                "Operations": [{ "op": "remove", "path": "members[value eq \"{{userId}}\"]" }]
            }
            """),
        });
        Assert.Equal(HttpStatusCode.OK, removeMember.StatusCode);

        // The built-in Admins group cannot be deleted.
        using var admins = await client.GetFromJsonAsync<JsonDocument>("/api/Groups?filter=" + Uri.EscapeDataString("displayName eq \"Admins\""));
        Assert.NotNull(admins);
        var adminGroupId = admins.RootElement.GetProperty("Resources")[0].GetProperty("id").GetString()!;
        using var deleteBuiltIn = await client.DeleteAsync($"/api/Groups/{adminGroupId}");
        Assert.Equal(HttpStatusCode.BadRequest, deleteBuiltIn.StatusCode);
        using var mutability = JsonDocument.Parse(await deleteBuiltIn.Content.ReadAsStringAsync());
        Assert.Equal("mutability", mutability.RootElement.GetProperty("scimType").GetString());

        // DELETE the created group.
        using var deleteResponse = await client.DeleteAsync($"/api/Groups/{groupId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        using var gone = await client.GetAsync($"/api/Groups/{groupId}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task PatchUser_RemovingAdminGroupFromLastAdmin_Is400()
    {
        using var anonymous = _factory.CreateAnonymousClient();
        using var client = ApiClient(await ServiceTokenAsync(anonymous));

        using var admins = await client.GetFromJsonAsync<JsonDocument>("/api/Groups?filter=" + Uri.EscapeDataString("displayName eq \"Admins\""));
        Assert.NotNull(admins);
        var adminGroupId = admins.RootElement.GetProperty("Resources")[0].GetProperty("id").GetString()!;
        using var users = await client.GetFromJsonAsync<JsonDocument>("/api/Users?filter=" + Uri.EscapeDataString($"userName eq \"{AnubisFactory.AdminEmail}\""));
        Assert.NotNull(users);
        var adminUserId = users.RootElement.GetProperty("Resources")[0].GetProperty("id").GetString()!;

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/Users/{adminUserId}")
        {
            Content = Scim($$"""
            {
                "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchRequest"],
                "Operations": [{ "op": "remove", "path": "groups[value eq \"{{adminGroupId}}\"]" }]
            }
            """),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("mutability", error.RootElement.GetProperty("scimType").GetString());
    }

    [Fact]
    public async Task CreateUser_WithUnknownGroup_Is400NoTarget()
    {
        using var anonymous = _factory.CreateAnonymousClient();
        using var client = ApiClient(await ServiceTokenAsync(anonymous));

        using var response = await client.PostAsync("/api/Users", Scim($$"""
        {
            "userName": "scim-notarget-{{Guid.NewGuid():N}}@test.local",
            "groups": [{ "value": "no-such-group-id" }]
        }
        """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("noTarget", error.RootElement.GetProperty("scimType").GetString());
    }

    [Fact]
    public async Task Attributes_Parameter_SelectsTopLevelAttributes()
    {
        using var anonymous = _factory.CreateAnonymousClient();
        using var client = ApiClient(await ServiceTokenAsync(anonymous));

        using var create = await client.PostAsync("/api/Users", Scim($$"""{ "userName": "scim-attrs-{{Guid.NewGuid():N}}@test.local", "displayName": "Attrs" }"""));
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetString()!;

        using var response = await client.GetFromJsonAsync<JsonDocument>($"/api/Users/{id}?attributes=userName,displayName");
        Assert.NotNull(response);
        Assert.Equal(4, response.RootElement.EnumerateObject().Count());   // schemas + id are always kept, plus the two requested
        Assert.True(response.RootElement.TryGetProperty("userName", out _));
        Assert.False(response.RootElement.TryGetProperty("emails", out _));
    }
}
