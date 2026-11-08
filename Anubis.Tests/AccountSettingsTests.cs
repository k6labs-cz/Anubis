using Anubis.Tests.Support;

using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Services;
using k6labs.Anubis.Services.Email;
using k6labs.Anubis.Services.Sms;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

/// <summary>
/// The account page: phone number verified by an SMS code, color mode saved on the
/// account (and applied before the first paint), and the language field.
/// </summary>
[Collection(AnubisCollection.Name)]
public class AccountSettingsTests
{
    private readonly AnubisFactory _factory;

    public AccountSettingsTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("+420 123 456 789", "+420123456789")]
    [InlineData("+420-123-456-789", "+420123456789")]
    [InlineData("00420 (123) 456.789", "+420123456789")]
    [InlineData("123 456 789", null)]      // no country code
    [InlineData("+0123456789", null)]
    [InlineData("+42", null)]
    [InlineData("", null)]
    public void PhoneNumbers_AreNormalizedToE164(string input, string? expected)
    {
        Assert.Equal(expected, PhoneNumberService.Normalize(input));
    }

    [Fact]
    public async Task PhoneNumber_IsSavedUnverified_VerifiedByCode_AndAChangeNeedsVerifyingAgain()
    {
        using var client = new FormClient(_factory);
        var (created, _) = await client.CreateUserAsync();
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var sms = new CapturingSmsSender();
        var phones = new PhoneNumberService(userManager, sms, scope.ServiceProvider.GetRequiredService<EmailThrottle>());
        var user = (await userManager.FindByIdAsync(created.Id))!;

        // Saving: invalid is refused, a valid number is stored (normalized) but not verified.
        Assert.False(await phones.SaveAsync(user, "123"));
        Assert.True(await phones.SaveAsync(user, "+420 123 456 789"));
        var saved = (await userManager.FindByIdAsync(user.Id))!;
        Assert.Equal("+420123456789", saved.PhoneNumber);
        Assert.False(saved.PhoneNumberConfirmed);

        // Verifying with the SMS code.
        Assert.Equal(PhoneCodeResult.Sent, await phones.SendCodeAsync(user, user.PhoneNumber));
        Assert.Equal(PhoneCodeResult.Throttled, await phones.SendCodeAsync(user, user.PhoneNumber));
        var (to, message) = Assert.Single(sms.Messages);
        Assert.Equal("+420123456789", to);
        var code = System.Text.RegularExpressions.Regex.Match(message, @"\d{6}").Value;
        Assert.NotEmpty(code);
        Assert.False(await phones.ConfirmAsync(user, user.PhoneNumber, "000000"));
        Assert.False(await phones.ConfirmAsync(user, "+420999999999", code));   // code is bound to the number
        Assert.True(await phones.ConfirmAsync(user, user.PhoneNumber, code));
        Assert.True((await userManager.FindByIdAsync(user.Id))!.PhoneNumberConfirmed);

        // Saving the same number (other formatting) keeps it verified; a new number does not.
        Assert.True(await phones.SaveAsync(user, "+420-123-456-789"));
        Assert.True((await userManager.FindByIdAsync(user.Id))!.PhoneNumberConfirmed);
        Assert.True(await phones.SaveAsync(user, "+420 987 654 321"));
        saved = (await userManager.FindByIdAsync(user.Id))!;
        Assert.Equal("+420987654321", saved.PhoneNumber);
        Assert.False(saved.PhoneNumberConfirmed);

        // An empty value removes the number.
        Assert.True(await phones.SaveAsync(user, " "));
        Assert.Null((await userManager.FindByIdAsync(user.Id))!.PhoneNumber);
    }

    [Fact]
    public async Task SavedColorScheme_IsTheHathorThemeMode()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();

        Assert.Contains("data-theme=\"system\"", await client.Http.GetStringAsync("/account"));

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var saved = (await userManager.FindByIdAsync(user.Id))!;
            saved.ColorScheme = ColorSchemes.Dark;
            await userManager.UpdateAsync(saved);
        }

        Assert.Contains("data-theme=\"dark\"", await client.Http.GetStringAsync("/account"));
    }

    [Fact]
    public async Task AnonymousPages_FollowTheSystemColorScheme()
    {
        using var client = _factory.CreateAnonymousClient();

        var html = await client.GetStringAsync("/login");

        Assert.Contains("data-theme=\"system\"", html);
    }

    [Fact]
    public async Task SignOut_IsANavigationLink_BackedByAPostForm()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();

        var account = await client.Http.GetStringAsync("/account");
        Assert.Contains("href=\"/logout\"", account);
        Assert.Contains("data-logout-form", account);

        // Without JavaScript the link opens a confirmation page; GET alone never signs out.
        var page = await client.Http.GetStringAsync("/logout");
        Assert.Contains("action=\"/auth/logout\"", page);
        Assert.True(await client.IsSignedInAsync());
    }

    [Fact]
    public async Task AccountPage_ShowsTheSettings()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();

        var html = await client.Http.GetStringAsync("/account");

        Assert.Contains("id=\"displayName\"", html);
        Assert.Contains(user.Email!, html);
        Assert.Contains("id=\"phone\"", html);
        Assert.Contains("Color mode", html);
        // No language chosen: the field shows the browser's preferred language (English here).
        Assert.Contains("Browser setting (English)", html);
        // No color mode chosen: follows the system.
        Assert.Contains("Not chosen: follows the light or dark setting of your system.", html);
    }

    private sealed class CapturingSmsSender : ISmsSender
    {
        public List<(string To, string Message)> Messages { get; } = new();

        public Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
        {
            Messages.Add((phoneNumber, message));
            return Task.CompletedTask;
        }
    }
}
