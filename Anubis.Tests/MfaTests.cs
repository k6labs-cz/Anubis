using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Anubis.Tests.Support;

using k6labs.Anubis.Data.Entities;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

/// <summary>End-to-end tests of passkeys, authenticator app (TOTP) and recovery codes over HTTP.</summary>
[Collection(AnubisCollection.Name)]
public partial class MfaTests
{
    private readonly AnubisFactory _factory;

    public MfaTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    // ---------------------------------------------------------------- passkeys

    [Fact]
    public async Task Passkey_CanBeRegistered_AndUsedToSignInWithoutPassword()
    {
        using var client = new FormClient(_factory);
        using var authenticator = new SoftwareAuthenticator();
        var (user, password) = await client.CreateUserAsync();

        await RegisterPasskeyAsync(client, authenticator, user, password);
        await client.SignOutAsync();
        Assert.False(await client.IsSignedInAsync());

        using var response = await PasskeyLoginAsync(client, authenticator);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/profile", RedirectPath(response));
        Assert.True(await client.IsSignedInAsync());
    }

    [Fact]
    public async Task Passkey_SignIn_SkipsAuthenticatorCode()
    {
        // A passkey with user verification is already two factors.
        using var client = new FormClient(_factory);
        using var authenticator = new SoftwareAuthenticator();
        var (user, password) = await client.CreateUserAsync();
        await EnableAuthenticatorAsync(user);

        // Password sign-in now needs a code; the passkey is added from that session.
        using (var login = await client.PasswordLoginAsync(user.Email!, password))
        {
            Assert.StartsWith("/login-2fa", RedirectPath(login));
        }

        await CompleteTwoFactorAsync(client, user);
        await AddPasskeyAsync(client, authenticator);
        await client.SignOutAsync();

        using var response = await PasskeyLoginAsync(client, authenticator);

        Assert.Equal("/profile", RedirectPath(response));
        Assert.True(await client.IsSignedInAsync());
    }

    [Fact]
    public async Task Passkey_WithBadSignature_IsRejected()
    {
        using var client = new FormClient(_factory);
        using var authenticator = new SoftwareAuthenticator();
        var (user, password) = await client.CreateUserAsync();
        await RegisterPasskeyAsync(client, authenticator, user, password);
        await client.SignOutAsync();

        using var response = await PasskeyLoginAsync(client, authenticator, signature => { signature[^1] ^= 0xFF; return signature; });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sign-in failed", await response.Content.ReadAsStringAsync());
        Assert.False(await client.IsSignedInAsync());
    }

    [Fact]
    public async Task Passkey_WithoutUserVerification_IsRejected()
    {
        // No fingerprint/PIN check, only "user present" - must not be accepted.
        using var client = new FormClient(_factory);
        using var authenticator = new SoftwareAuthenticator();
        var (user, password) = await client.CreateUserAsync();
        await RegisterPasskeyAsync(client, authenticator, user, password);
        await client.SignOutAsync();

        authenticator.VerifyUser = false;
        using var response = await PasskeyLoginAsync(client, authenticator);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await client.IsSignedInAsync());
    }

    [Fact]
    public async Task Passkey_CanBeDeleted()
    {
        using var client = new FormClient(_factory);
        using var authenticator = new SoftwareAuthenticator();
        var (user, password) = await client.CreateUserAsync();
        await RegisterPasskeyAsync(client, authenticator, user, password);

        using var response = await client.PostFormAsync("/passkeys", $"delete-passkey-{authenticator.CredentialId}", new Dictionary<string, string>
        {
            ["CredentialId"] = authenticator.CredentialId,
        });

        Assert.Equal("/passkeys?status=passkey-deleted", RedirectPath(response));
        Assert.Empty(await GetPasskeysAsync(user));
    }

    [Fact]
    public async Task PasskeyCreationOptions_RequireSignIn()
    {
        using var client = new FormClient(_factory);

        using var response = await PostOptionsAsync(client, "/auth/passkey/creation-options", "/login");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PasskeyRequestOptions_DoNotRevealAccounts()
    {
        using var client = new FormClient(_factory);

        using var response = await PostOptionsAsync(client, "/auth/passkey/request-options", "/login");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"allowCredentials\":[]", json);
        Assert.Contains("\"userVerification\":\"required\"", json);
    }

    // ---------------------------------------------------------------- authenticator app

    [Fact]
    public async Task AuthenticatorApp_SetupFlow_EnablesTwoFactor_AndShowsRecoveryCodes()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        await client.PasswordLoginAsync(user.Email!, password);

        var page = await client.Http.GetStringAsync("/mfa/authenticator");
        Assert.Contains("data:image/png;base64,", page);
        var key = KeyRegex().Match(page).Groups[1].Value.Replace(" ", string.Empty);

        using var wrong = await client.PostFormAsync("/mfa/authenticator", "verify-authenticator", new Dictionary<string, string>
        {
            ["model.Code"] = "000000",
        });
        Assert.Contains("The code is invalid", await wrong.Content.ReadAsStringAsync());

        using var right = await client.PostFormAsync("/mfa/authenticator", "verify-authenticator", new Dictionary<string, string>
        {
            ["model.Code"] = Totp(key),
        });
        var html = await right.Content.ReadAsStringAsync();

        Assert.Contains("Save these recovery codes now", html);
        Assert.Equal(10, RecoveryCodeRegex().Matches(html).Count);
        Assert.True(await IsTwoFactorEnabledAsync(user));
        // Enabling 2FA changes the security stamp; the session must survive that.
        Assert.True(await client.IsSignedInAsync());
    }

    [Fact]
    public async Task PasswordLogin_WithTwoFactor_RequiresValidCode()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        var key = await EnableAuthenticatorAsync(user);

        using var login = await client.PasswordLoginAsync(user.Email!, password);
        Assert.Equal("/login-2fa?returnUrl=%2Fprofile", RedirectPath(login));
        Assert.False(await client.IsSignedInAsync());

        using var wrong = await PostTwoFactorCodeAsync(client, "123456" == Totp(key) ? "654321" : "123456");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.False(await client.IsSignedInAsync());

        using var right = await PostTwoFactorCodeAsync(client, Totp(key));
        Assert.Equal("/profile", RedirectPath(right));
        Assert.True(await client.IsSignedInAsync());
    }

    [Fact]
    public async Task TwoFactorPage_WithoutPasswordStep_RedirectsToLogin()
    {
        using var client = new FormClient(_factory);

        using var response = await client.Http.GetAsync("/login-2fa?returnUrl=/profile");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", RedirectPath(response));
    }

    [Fact]
    public async Task RecoveryCode_SignsInOnce()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        await EnableAuthenticatorAsync(user);
        var code = (await GenerateRecoveryCodesAsync(user)).First();

        await client.PasswordLoginAsync(user.Email!, password);
        using (var first = await PostRecoveryCodeAsync(client, code))
        {
            Assert.Equal("/profile", RedirectPath(first));
        }

        Assert.True(await client.IsSignedInAsync());
        await client.SignOutAsync();

        await client.PasswordLoginAsync(user.Email!, password);
        using var second = await PostRecoveryCodeAsync(client, code);
        Assert.Contains("already been used", await second.Content.ReadAsStringAsync());
        Assert.False(await client.IsSignedInAsync());
    }

    [Fact]
    public async Task DisableTwoFactor_LetsPasswordLoginSucceedDirectly()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        await EnableAuthenticatorAsync(user);
        await client.PasswordLoginAsync(user.Email!, password);
        await CompleteTwoFactorAsync(client, user);

        using var disable = await client.PostFormAsync("/mfa/disable", "disable-2fa", new Dictionary<string, string>());
        Assert.Equal("/passkeys?status=mfa-disabled", RedirectPath(disable));
        Assert.False(await IsTwoFactorEnabledAsync(user));
        Assert.True(await client.IsSignedInAsync());

        await client.SignOutAsync();
        using var login = await client.PasswordLoginAsync(user.Email!, password);
        Assert.Equal("/profile", RedirectPath(login));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Redirect target as path + query (Blazor sends absolute URLs).</summary>
    private static string? RedirectPath(HttpResponseMessage response)
        => response.Headers.Location is { } location
            ? location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString
            : null;

    private async Task RegisterPasskeyAsync(FormClient client, SoftwareAuthenticator authenticator, User user, string password)
    {
        using (var login = await client.PasswordLoginAsync(user.Email!, password))
        {
            Assert.Equal("/profile", RedirectPath(login));
        }

        await AddPasskeyAsync(client, authenticator);
        Assert.Single(await GetPasskeysAsync(user));
    }

    private static async Task AddPasskeyAsync(FormClient client, SoftwareAuthenticator authenticator)
    {
        using var options = await PostOptionsAsync(client, "/auth/passkey/creation-options", "/passkeys");
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        var credentialJson = authenticator.Create(await options.Content.ReadAsStringAsync());

        using var response = await client.PostFormAsync("/passkeys", "add-passkey", new Dictionary<string, string>
        {
            ["AddPasskey.CredentialJson"] = credentialJson,
        });

        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        Assert.StartsWith("/passkeys/", RedirectPath(response));
    }

    private static async Task<HttpResponseMessage> PasskeyLoginAsync(FormClient client, SoftwareAuthenticator authenticator, Func<byte[], byte[]>? tamper = null)
    {
        using var options = await PostOptionsAsync(client, "/auth/passkey/request-options", "/login");
        var credentialJson = authenticator.Get(await options.Content.ReadAsStringAsync(), tamper);

        return await client.PostFormAsync("/login?returnUrl=%2Fprofile", "login", new Dictionary<string, string>
        {
            ["model.Passkey.CredentialJson"] = credentialJson,
        });
    }

    private static async Task<HttpResponseMessage> PostOptionsAsync(FormClient client, string url, string pageWithToken)
    {
        var token = await client.GetTokenAsync(pageWithToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("RequestVerificationToken", token);
        return await client.Http.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostTwoFactorCodeAsync(FormClient client, string code)
        => client.PostFormAsync("/login-2fa?returnUrl=%2Fprofile", "login-2fa", new Dictionary<string, string>
        {
            ["model.Code"] = code,
        });

    private static Task<HttpResponseMessage> PostRecoveryCodeAsync(FormClient client, string code)
        => client.PostFormAsync("/login-recovery-code?returnUrl=%2Fprofile", "login-recovery", new Dictionary<string, string>
        {
            ["model.RecoveryCode"] = code,
        });

    private async Task CompleteTwoFactorAsync(FormClient client, User user)
    {
        using var response = await PostTwoFactorCodeAsync(client, Totp(await GetAuthenticatorKeyAsync(user)));
        Assert.Equal("/profile", RedirectPath(response));
    }

    private async Task<string> EnableAuthenticatorAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var fresh = (await userManager.FindByIdAsync(user.Id))!;
        await userManager.ResetAuthenticatorKeyAsync(fresh);
        await userManager.SetTwoFactorEnabledAsync(fresh, true);
        return (await userManager.GetAuthenticatorKeyAsync(fresh))!;
    }

    private async Task<string> GetAuthenticatorKeyAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        return (await userManager.GetAuthenticatorKeyAsync((await userManager.FindByIdAsync(user.Id))!))!;
    }

    private async Task<IEnumerable<string>> GenerateRecoveryCodesAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        return (await userManager.GenerateNewTwoFactorRecoveryCodesAsync((await userManager.FindByIdAsync(user.Id))!, 10))!;
    }

    private async Task<bool> IsTwoFactorEnabledAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        return await userManager.GetTwoFactorEnabledAsync((await userManager.FindByIdAsync(user.Id))!);
    }

    private async Task<IList<UserPasskeyInfo>> GetPasskeysAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        return await userManager.GetPasskeysAsync((await userManager.FindByIdAsync(user.Id))!);
    }

    /// <summary>RFC 6238 TOTP (SHA-1, 6 digits, 30 s), as computed by authenticator apps.</summary>
    private static string Totp(string base32Key)
    {
        var key = Base32Decode(base32Key);
        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var value = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF;
        return (value % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var buffer = 0;
        var output = new List<byte>();
        foreach (var c in input.ToUpperInvariant().TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }

    [GeneratedRegex("<code data-secret>([^<]+)</code>")]
    private static partial Regex KeyRegex();

    [GeneratedRegex("<code data-recovery-code>[^<]+</code>")]
    private static partial Regex RecoveryCodeRegex();
}
