using System.Net;

using Anubis.Tests.Support;

using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Options;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

/// <summary>Password reset and email confirmation, end to end, with captured emails.</summary>
[Collection(AnubisCollection.Name)]
public class AccountEmailTests
{
    private const string NewPassword = "Brand-New-Password-456!";

    private readonly AnubisFactory _factory;

    public AccountEmailTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ForgotPassword_EmailsLink_ThatResetsThePasswordOnce()
    {
        using var client = new FormClient(_factory);
        var (user, oldPassword) = await client.CreateUserAsync();

        using (var forgot = await RequestResetAsync(client, user.Email!))
        {
            Assert.Contains("Check your inbox", await forgot.Content.ReadAsStringAsync());
        }

        var link = _factory.Emails.LastLinkTo(user.Email!);
        Assert.StartsWith("/reset-password?email=", link);
        Assert.Equal("Reset your password", _factory.Emails.To(user.Email!).Last().Subject);

        using (var reset = await ResetAsync(client, link, NewPassword))
        {
            Assert.Contains("Password changed", await reset.Content.ReadAsStringAsync());
        }

        using (var oldLogin = await client.PasswordLoginAsync(user.Email!, oldPassword))
        {
            Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
        }

        using (var newLogin = await client.PasswordLoginAsync(user.Email!, NewPassword))
        {
            Assert.Equal(HttpStatusCode.Redirect, newLogin.StatusCode);
        }

        // The security stamp changed, so the same link can't be used again.
        using var again = await ResetAsync(client, link, "Another-Password-789!");
        Assert.Contains("expired or was already used", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ForgotPassword_UnknownEmail_GivesSameAnswer_AndSendsNothing()
    {
        using var client = new FormClient(_factory);
        var email = $"nobody-{Guid.NewGuid():N}@test.local";

        using var response = await RequestResetAsync(client, email);

        Assert.Contains("Check your inbox", await response.Content.ReadAsStringAsync());
        Assert.Empty(_factory.Emails.To(email));
    }

    [Fact]
    public async Task ForgotPassword_IsThrottledPerAccount()
    {
        using var client = new FormClient(_factory);
        var (user, _) = await client.CreateUserAsync();

        (await RequestResetAsync(client, user.Email!)).Dispose();
        (await RequestResetAsync(client, user.Email!)).Dispose();

        Assert.Single(_factory.Emails.To(user.Email!));
    }

    [Fact]
    public async Task ResetPassword_WithTamperedCode_Fails()
    {
        using var client = new FormClient(_factory);
        var (user, _) = await client.CreateUserAsync();
        (await RequestResetAsync(client, user.Email!)).Dispose();
        var link = _factory.Emails.LastLinkTo(user.Email!);

        using var response = await ResetAsync(client, link[..^4] + "AAAA", NewPassword);

        var html = await response.Content.ReadAsStringAsync();
        Assert.True(html.Contains("expired or was already used") || html.Contains("Invalid link"));
    }

    [Fact]
    public async Task Register_SendsConfirmationLink_ThatConfirmsTheAddress()
    {
        using var client = new FormClient(_factory);
        var email = $"new-{Guid.NewGuid():N}@test.local";

        using (var register = await RegisterAsync(client, email))
        {
            // Confirmation is optional by default, so the user is signed in right away.
            Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);
        }

        var link = _factory.Emails.LastLinkTo(email);
        Assert.StartsWith("/confirm-email?userId=", link);

        var page = await client.Http.GetStringAsync(link);

        Assert.Contains("Email confirmed", page);
        Assert.True((await FindUserAsync(_factory, email))!.EmailConfirmed);
    }

    [Fact]
    public async Task ConfirmEmail_WithBadCode_ShowsError()
    {
        using var client = new FormClient(_factory);
        var email = $"unconfirmed-{Guid.NewGuid():N}@test.local";
        (await RegisterAsync(client, email)).Dispose();
        var user = (await FindUserAsync(_factory, email))!;

        var page = await client.Http.GetStringAsync($"/confirm-email?userId={user.Id}&code=bm90LWEtdG9rZW4");

        Assert.Contains("invalid or has expired", page);
        Assert.False((await FindUserAsync(_factory, email))!.EmailConfirmed);
    }

    private static Task<HttpResponseMessage> RequestResetAsync(FormClient client, string email)
        => client.PostFormAsync("/forgot-password", "forgot-password", new Dictionary<string, string>
        {
            ["model.Email"] = email,
        });

    private static Task<HttpResponseMessage> ResetAsync(FormClient client, string link, string password)
        => client.PostFormAsync(link, "reset-password", new Dictionary<string, string>
        {
            ["model.Password"] = password,
            ["model.ConfirmPassword"] = password,
        });

    internal static Task<HttpResponseMessage> RegisterAsync(FormClient client, string email)
        => client.PostFormAsync("/register", "register", new Dictionary<string, string>
        {
            ["model.DisplayName"] = "New User",
            ["model.Email"] = email,
            ["model.Password"] = NewPassword,
            ["model.ConfirmPassword"] = NewPassword,
        });

    internal static async Task<User?> FindUserAsync(AnubisFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByEmailAsync(email);
    }
}

/// <summary>The same app with <c>Anubis:RequireConfirmedEmail</c> turned on.</summary>
public sealed class RequireConfirmedEmailFactory : AnubisFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.Configure<AnubisOptions>(o => o.RequireConfirmedEmail = true));
    }
}

// Same collection as the other tests: both hosts share one SQLite file, so they must not run in parallel.
[Collection(AnubisCollection.Name)]
public class RequireConfirmedEmailTests : IClassFixture<RequireConfirmedEmailFactory>
{
    private readonly RequireConfirmedEmailFactory _factory;

    public RequireConfirmedEmailTests(RequireConfirmedEmailFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UnconfirmedUser_CannotSignIn_UntilTheyConfirm()
    {
        using var client = new FormClient(_factory);
        var email = $"confirm-{Guid.NewGuid():N}@test.local";

        using (var register = await AccountEmailTests.RegisterAsync(client, email))
        {
            Assert.Equal(HttpStatusCode.OK, register.StatusCode);
            Assert.Contains("We sent a confirmation link", await register.Content.ReadAsStringAsync());
        }

        Assert.False(await client.IsSignedInAsync());

        using (var blocked = await client.PasswordLoginAsync(email, "Brand-New-Password-456!"))
        {
            Assert.Contains("Confirm your email address first", await blocked.Content.ReadAsStringAsync());
        }

        await client.Http.GetStringAsync(_factory.Emails.LastLinkTo(email));

        using var allowed = await client.PasswordLoginAsync(email, "Brand-New-Password-456!");
        Assert.Equal(HttpStatusCode.Redirect, allowed.StatusCode);
    }
}
