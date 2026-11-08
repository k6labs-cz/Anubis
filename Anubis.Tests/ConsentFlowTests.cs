using System.Net;
using System.Security.Cryptography;
using System.Text;

using Anubis.Tests.Support;

using Microsoft.AspNetCore.WebUtilities;

namespace Anubis.Tests;

/// <summary>Authorization code flow through the consent page (the seeded web client requires consent).</summary>
[Collection(AnubisCollection.Name)]
public class ConsentFlowTests
{
    private readonly AnubisFactory _factory;

    public ConsentFlowTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Allow_ReturnsAnAuthorizationCodeToTheClient()
    {
        using var client = new FormClient(_factory);
        var consentUrl = await StartAsync(client);

        using var decision = await DecideAsync(client, consentUrl, "allow");
        Assert.StartsWith("/authorize", decision.Headers.Location?.OriginalString);

        // Back at the authorize endpoint, consent is now on record: the client gets a code.
        using var authorize = await client.Http.GetAsync(decision.Headers.Location);
        var callback = authorize.Headers.Location!;
        Assert.StartsWith(AnubisFactory.WebClientRedirectUri, callback.ToString());
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.False(string.IsNullOrEmpty(query["code"]));
        Assert.Equal("xyz", query["state"]);
    }

    [Fact]
    public async Task Deny_ReturnsAccessDeniedToTheClient()
    {
        using var client = new FormClient(_factory);
        var consentUrl = await StartAsync(client);

        using var decision = await DecideAsync(client, consentUrl, "deny");

        var callback = decision.Headers.Location!;
        Assert.StartsWith(AnubisFactory.WebClientRedirectUri, callback.ToString());
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.Equal("access_denied", query["error"]);
        Assert.Equal("xyz", query["state"]);
    }

    [Fact]
    public async Task ConsentPage_DescribesScopes_InTheUsersLanguage()
    {
        using var client = new FormClient(_factory, "cs");
        var consentUrl = await StartAsync(client);

        var html = await client.Http.GetStringAsync(consentUrl);

        Assert.Contains("<html lang=\"cs\"", html);
        Assert.Contains("Ověřit, kdo jste", html);
        Assert.Contains("Zobrazit vaši e-mailovou adresu", html);
    }

    /// <summary>Signs in a fresh user and starts the code flow; returns the /consent URL.</summary>
    private static async Task<string> StartAsync(FormClient client)
    {
        var (user, password) = await client.CreateUserAsync();
        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();

        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var url = "/authorize?" + string.Join('&',
            $"client_id={AnubisFactory.WebClientId}",
            $"redirect_uri={Uri.EscapeDataString(AnubisFactory.WebClientRedirectUri)}",
            "response_type=code",
            "scope=openid%20email",
            "state=xyz",
            "nonce=abc",
            $"code_challenge={challenge}",
            "code_challenge_method=S256");

        using var response = await client.Http.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/consent?returnUrl=", location);
        return location;
    }

    private static async Task<HttpResponseMessage> DecideAsync(FormClient client, string consentUrl, string decision)
    {
        var html = await client.Http.GetStringAsync(consentUrl);
        var returnUrl = QueryHelpers.ParseQuery(new Uri("http://localhost" + consentUrl).Query)["returnUrl"].ToString();

        return await client.Http.PostAsync("/auth/consent", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = FormClient.ReadToken(html),
            ["returnUrl"] = returnUrl,
            ["decision"] = decision,
        }));
    }
}
