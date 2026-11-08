using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Anubis.Tests.Support;

using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Models;
using k6labs.Anubis.Services;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

/// <summary>
/// Custom claims on groups and users: validated (reserved types refused), saved, and issued
/// in the ID token and userinfo of the users that have them.
/// </summary>
[Collection(AnubisCollection.Name)]
public class ClaimsTests
{
    private readonly AnubisFactory _factory;

    public ClaimsTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("email")]
    [InlineData("ROLE")]
    [InlineData("oi_scp")]
    [InlineData("http://schemas.microsoft.com/ws/2008/06/identity/claims/role")]
    [InlineData("AspNet.Identity.SecurityStamp")]
    public void ReservedClaimTypes_AreRefused(string type)
    {
        var errors = new Dictionary<string, string>();
        CustomClaims.Validate([new ClaimEntry { Type = type, Value = "x" }], errors);
        Assert.True(errors.ContainsKey("Claims[0]"));
    }

    [Fact]
    public void InvalidRows_GetAnErrorEach_EmptyRowsAreIgnored()
    {
        var errors = new Dictionary<string, string>();
        var claims = CustomClaims.Validate(
        [
            new ClaimEntry { Type = " department ", Value = " sales " },
            new ClaimEntry(),                                           // empty: ignored
            new ClaimEntry { Type = "1abc", Value = "x" },              // must start with a letter
            new ClaimEntry { Type = "level", Value = "" },              // value required
            new ClaimEntry { Type = "department", Value = "sales" },    // duplicate
            new ClaimEntry { Type = "note", Value = new string('x', CustomClaims.ValueMaxLength + 1) },
        ], errors);

        Assert.Equal(("department", "sales"), (Assert.Single(claims).Type, claims[0].Value));
        Assert.Equal(["Claims[2]", "Claims[3]", "Claims[4]", "Claims[5]"], errors.Keys.Order());
    }

    [Fact]
    public async Task GroupEdit_RenamesAndSavesClaims_ButNotForBuiltInGroups()
    {
        using var scope = _factory.Services.CreateScope();
        var groups = scope.ServiceProvider.GetRequiredService<RoleManager<Group>>();
        var administration = scope.ServiceProvider.GetRequiredService<GroupAdministration>();
        var name = $"team-{Guid.NewGuid():N}"[..20];
        Assert.True((await groups.CreateAsync(new Group(name))).Succeeded);
        var group = (await groups.FindByNameAsync(name))!;

        var model = (await administration.LoadAsync(group.Id))!;
        model.Name = name + "-x";
        model.Claims.Add(new ClaimEntry { Type = "department", Value = "sales" });
        Assert.Empty(await administration.UpdateAsync(model));

        var saved = (await groups.FindByIdAsync(group.Id))!;
        Assert.Equal(name + "-x", saved.Name);
        Assert.Equal("sales", Assert.Single(await groups.GetClaimsAsync(saved)).Value);

        var admin = (await administration.LoadAsync((await groups.FindByNameAsync("Admins"))!.Id))!;
        admin.Name = "Administrators";
        Assert.True((await administration.UpdateAsync(admin)).ContainsKey(nameof(GroupEditModel.Name)));

        model = (await administration.LoadAsync(group.Id))!;
        model.Name = "Users";   // taken by a built-in group
        Assert.True((await administration.UpdateAsync(model)).ContainsKey(nameof(GroupEditModel.Name)));
    }

    [Fact]
    public async Task GroupAndUserClaims_AreInTheIdToken_AndUserinfo()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        var groupName = $"claims-{Guid.NewGuid():N}"[..20];
        using (var scope = _factory.Services.CreateScope())
        {
            var groups = scope.ServiceProvider.GetRequiredService<RoleManager<Group>>();
            Assert.True((await groups.CreateAsync(new Group(groupName))).Succeeded);
            await groups.AddClaimAsync((await groups.FindByNameAsync(groupName))!, new Claim("department", "sales"));

            var administration = scope.ServiceProvider.GetRequiredService<UserAdministration>();
            var model = (await administration.LoadAsync(user.Id))!;
            model.Groups.Add(groupName);
            model.Claims.Add(new ClaimEntry { Type = "employee_id", Value = "E-42" });
            model.Claims.Add(new ClaimEntry { Type = "department", Value = "support" });
            Assert.True((await administration.UpdateAsync(model, actingUserId: null)).Succeeded);
        }

        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();
        var (idToken, accessToken) = await SignInWithCodeFlowAsync(client);

        using var payload = JsonDocument.Parse(Base64UrlDecode(idToken.Split('.')[1]));
        Assert.Equal("E-42", payload.RootElement.GetProperty("employee_id").GetString());
        Assert.Equal(["sales", "support"], payload.RootElement.GetProperty("department").EnumerateArray().Select(v => v.GetString()).Order());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var userinfo = await client.Http.SendAsync(request);
        using var info = JsonDocument.Parse(await userinfo.Content.ReadAsStringAsync());
        Assert.Equal("E-42", info.RootElement.GetProperty("employee_id").GetString());
        Assert.Equal(user.Id, info.RootElement.GetProperty("sub").GetString());
    }

    private static async Task<(string IdToken, string AccessToken)> SignInWithCodeFlowAsync(FormClient client)
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var url = "/authorize?" + string.Join('&',
            $"client_id={AnubisFactory.WebClientId}",
            $"redirect_uri={Uri.EscapeDataString(AnubisFactory.WebClientRedirectUri)}",
            "response_type=code", "scope=openid%20profile", "state=s", "nonce=n",
            $"code_challenge={challenge}", "code_challenge_method=S256");

        using var consentRedirect = await client.Http.GetAsync(url);
        var consentUrl = consentRedirect.Headers.Location!.OriginalString;
        var html = await client.Http.GetStringAsync(consentUrl);
        using var decision = await client.Http.PostAsync("/auth/consent", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = FormClient.ReadToken(html),
            ["returnUrl"] = QueryHelpers.ParseQuery(new Uri("http://localhost" + consentUrl).Query)["returnUrl"].ToString(),
            ["decision"] = "allow",
        }));
        using var authorize = await client.Http.GetAsync(decision.Headers.Location);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();

        using var token = await client.Http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = AnubisFactory.WebClientId,
            ["client_secret"] = AnubisFactory.WebClientSecret,
            ["code"] = code,
            ["redirect_uri"] = AnubisFactory.WebClientRedirectUri,
            ["code_verifier"] = verifier,
        }));
        using var json = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("id_token").GetString()!, json.RootElement.GetProperty("access_token").GetString()!);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
