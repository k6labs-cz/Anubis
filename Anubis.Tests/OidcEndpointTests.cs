using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Anubis.Tests.Support;

namespace Anubis.Tests;

[Collection(AnubisCollection.Name)]
public class OidcEndpointTests
{
    private readonly AnubisFactory _factory;

    public OidcEndpointTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DiscoveryDocument_IsPublished()
    {
        using var client = _factory.CreateAnonymousClient();

        using var document = await client.GetFromJsonAsync<JsonDocument>("/.well-known/openid-configuration");
        Assert.NotNull(document);

        var root = document.RootElement;
        Assert.False(string.IsNullOrEmpty(root.GetProperty("issuer").GetString()));
        Assert.Contains("authorization_endpoint", root.ToString());
        Assert.Contains("/authorize", root.GetProperty("authorization_endpoint").GetString());
        Assert.Contains("/token", root.GetProperty("token_endpoint").GetString());
        Assert.Contains("/userinfo", root.GetProperty("userinfo_endpoint").GetString());
        Assert.Contains("/endsession", root.GetProperty("end_session_endpoint").GetString());
        Assert.Contains("authorization_code", root.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("client_credentials", root.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("S256", root.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Authorize_WithoutSession_RedirectsToLogin()
    {
        using var client = _factory.CreateAnonymousClient();

        var challenge = CreatePkceChallenge();
        var url = "/authorize?" + string.Join('&', new[]
        {
            $"client_id={AnubisFactory.WebClientId}",
            $"redirect_uri={Uri.EscapeDataString(AnubisFactory.WebClientRedirectUri)}",
            "response_type=code",
            "scope=openid%20profile%20email",
            "state=xyz",
            "nonce=abc",
            $"code_challenge={challenge}",
            "code_challenge_method=S256",
        });

        using var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? string.Empty;
        Assert.Contains("/login", location);
        Assert.Contains("returnUrl=", location);
    }

    [Fact]
    public async Task Token_ClientCredentials_ReturnsAccessToken()
    {
        using var client = _factory.CreateAnonymousClient();

        using var response = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AnubisFactory.ServiceClientId,
            ["client_secret"] = AnubisFactory.ServiceClientSecret,
            ["scope"] = "anubis-api",
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Bearer", payload.RootElement.GetProperty("token_type").GetString());
        Assert.False(string.IsNullOrEmpty(payload.RootElement.GetProperty("access_token").GetString()));
        Assert.True(payload.RootElement.TryGetProperty("expires_in", out _));
    }

    [Fact]
    public async Task Token_ClientCredentials_WithWrongSecret_IsRejected()
    {
        using var client = _factory.CreateAnonymousClient();

        using var response = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AnubisFactory.ServiceClientId,
            ["client_secret"] = "wrong-secret",
            ["scope"] = "anubis-api",
        }));

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_client", payload.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Introspection_WithClientCredentialsToken_ReportsActiveToken()
    {
        using var client = _factory.CreateAnonymousClient();

        using var tokenResponse = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AnubisFactory.ServiceClientId,
            ["client_secret"] = AnubisFactory.ServiceClientSecret,
            ["scope"] = "anubis-api",
        }));

        using var tokenPayload = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        var accessToken = tokenPayload.RootElement.GetProperty("access_token").GetString()!;

        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{AnubisFactory.ServiceClientId}:{AnubisFactory.ServiceClientSecret}"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/introspect")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = accessToken }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Status: {response.StatusCode}, body: {body}");

        using var payload = JsonDocument.Parse(body);
        Assert.True(payload.RootElement.GetProperty("active").GetBoolean());
        Assert.Equal(AnubisFactory.ServiceClientId, payload.RootElement.GetProperty("sub").GetString());
        Assert.Contains("anubis-api", payload.RootElement.GetProperty("aud").GetString());
    }

    [Fact]
    public async Task Userinfo_WithClientCredentialsToken_IsRejected_NoUserSubject()
    {
        // The userinfo endpoint is meant for user tokens; a client-credentials token
        // has no user subject, so the request is rejected.
        using var client = _factory.CreateAnonymousClient();

        using var tokenResponse = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AnubisFactory.ServiceClientId,
            ["client_secret"] = AnubisFactory.ServiceClientSecret,
            ["scope"] = "anubis-api",
        }));

        using var tokenPayload = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        var accessToken = tokenPayload.RootElement.GetProperty("access_token").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Userinfo_WithoutToken_IsUnauthorized()
    {
        using var client = _factory.CreateAnonymousClient();

        using var response = await client.GetAsync("/userinfo");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthCheck_ReturnsHealthy()
    {
        using var client = _factory.CreateAnonymousClient();

        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Index_RedirectsToLogin_WhenAnonymous()
    {
        using var client = _factory.CreateAnonymousClient();

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Index_RedirectsToAccount_WhenAuthenticated()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();

        using var response = await client.Http.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/profile", response.Headers.Location?.ToString());
    }

    private static string CreatePkceChallenge()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Convert.ToBase64String(verifierBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(challengeBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
