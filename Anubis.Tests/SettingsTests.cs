using System.Text.Json;
using System.Text.Json.Nodes;

using Anubis.Tests.Support;

using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Options;
using k6labs.Anubis.Services;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

/// <summary>
/// The admin settings (General / Security / Tokens) are saved to the settings file and apply
/// without a restart. Every test restores what it changed (the factory is shared).
/// </summary>
[Collection(AnubisCollection.Name)]
public class SettingsTests
{
    private readonly AnubisFactory _factory;

    public SettingsTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    private SettingsFile Settings => _factory.Services.GetRequiredService<SettingsFile>();

    [Fact]
    public async Task PasswordPolicy_AppliesImmediately_IncludingTheHints()
    {
        try
        {
            await Settings.SaveSectionAsync(SecurityOptions.SectionName, new JsonObject { ["PasswordMinLength"] = 20, ["PasswordRequireNonAlphanumeric"] = true });

            using var scope = _factory.Services.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var email = $"policy-{Guid.NewGuid():N}@test.local";
            var tooShort = await userManager.CreateAsync(new User { UserName = email, Email = email }, "Short-Passw0rd");
            Assert.False(tooShort.Succeeded);
            Assert.Contains(tooShort.Errors, e => e.Code == nameof(IdentityErrorDescriber.PasswordTooShort));
            Assert.True((await userManager.CreateAsync(new User { UserName = email, Email = email }, "A-Much-Longer-Passw0rd!")).Succeeded);

            using var client = _factory.CreateAnonymousClient();
            Assert.Contains("At least 20 characters", await client.GetStringAsync("/register"));
        }
        finally
        {
            await Settings.SaveSectionAsync(SecurityOptions.SectionName, new JsonObject());
        }
    }

    [Fact]
    public async Task Lockout_AndSessionLength_ApplyImmediately()
    {
        try
        {
            await Settings.SaveSectionAsync(SecurityOptions.SectionName, new JsonObject { ["LockoutMaxFailedAttempts"] = 2, ["SessionLifetimeHours"] = 2 });

            using var client = new FormClient(_factory);
            var (user, password) = await client.CreateUserAsync();

            using (var signIn = await client.PasswordLoginAsync(user.Email!, password))
            {
                var cookie = signIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("anubis.auth=", StringComparison.Ordinal));
                var expires = DateTimeOffset.Parse(cookie.Split("expires=")[1].Split(';')[0], System.Globalization.CultureInfo.InvariantCulture);
                Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromHours(1.9), TimeSpan.FromHours(2.1));
            }

            await client.SignOutAsync();
            (await client.PasswordLoginAsync(user.Email!, "wrong-password-123!")).Dispose();
            (await client.PasswordLoginAsync(user.Email!, "wrong-password-123!")).Dispose();

            using var scope = _factory.Services.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True(await userManager.IsLockedOutAsync((await userManager.FindByIdAsync(user.Id))!));   // after 2, not 5
        }
        finally
        {
            await Settings.SaveSectionAsync(SecurityOptions.SectionName, new JsonObject());
        }
    }

    [Fact]
    public async Task AccessTokenLifetime_AppliesToTheNextToken()
    {
        try
        {
            await Settings.UpdateSectionAsync(AnubisOptions.SectionName, new JsonObject { ["AccessTokenLifetimeMinutes"] = 7 });

            using var client = _factory.CreateAnonymousClient();
            using var response = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = AnubisFactory.ServiceClientId,
                ["client_secret"] = AnubisFactory.ServiceClientSecret,
                ["scope"] = "anubis-api",
            }));

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.InRange(payload.RootElement.GetProperty("expires_in").GetInt32(), 7 * 60 - 5, 7 * 60);
        }
        finally
        {
            await RemoveAnubisKeysAsync("AccessTokenLifetimeMinutes");
        }
    }

    [Fact]
    public async Task Registration_CanBeTurnedOff_WithoutTouchingOtherSettings()
    {
        try
        {
            await Settings.SaveSectionAsync(AppearanceOptions.SectionName, new JsonObject { ["FontFamily"] = "KeepMe" });
            await Settings.UpdateSectionAsync(AnubisOptions.SectionName, new JsonObject { ["AllowRegistration"] = false });

            using var client = _factory.CreateAnonymousClient();
            Assert.Contains("Registration disabled", await client.GetStringAsync("/register"));
            Assert.Equal("KeepMe", (await Settings.ReadSectionAsync(AppearanceOptions.SectionName))?["FontFamily"]?.GetValue<string>());
        }
        finally
        {
            await RemoveAnubisKeysAsync("AllowRegistration");
            await Settings.SaveSectionAsync(AppearanceOptions.SectionName, new JsonObject());
        }
    }

    [Theory]
    [InlineData("/admin/general", "id=\"publicUrl\"")]
    [InlineData("/admin/security", "Minimum length")]
    [InlineData("/admin/tokens", "Access token (minutes)")]
    public async Task SettingsPages_ShowTheirOptions(string path, string expected)
    {
        using var client = new FormClient(_factory);
        (await client.PasswordLoginAsync(AnubisFactory.AdminEmail, AnubisFactory.AdminPassword)).Dispose();

        Assert.Contains(expected, await client.Http.GetStringAsync(path));
    }

    [Theory]
    [InlineData("/admin/general")]
    [InlineData("/admin/security")]
    [InlineData("/admin/tokens")]
    [InlineData("/admin/appearance")]
    [InlineData("/admin/email")]
    [InlineData("/account")]
    public async Task SettingsPages_HaveOneSaveButtonAtTheBottom_DisabledUntilSomethingChanges(string path)
    {
        using var client = new FormClient(_factory);
        (await client.PasswordLoginAsync(AnubisFactory.AdminEmail, AnubisFactory.AdminPassword)).Dispose();

        var html = await client.Http.GetStringAsync(path);
        var start = html.IndexOf("data-save-button", StringComparison.Ordinal);
        Assert.True(start > 0, "no save button");
        Assert.Equal(start, html.LastIndexOf("data-save-button", StringComparison.Ordinal));   // exactly one
        var tagStart = html.LastIndexOf('<', start);
        Assert.Contains("disabled", html[tagStart..html.IndexOf('>', start)]);
    }

    /// <summary>Removes keys of the "Anubis" section from the settings file (back to appsettings / environment).</summary>
    private async Task RemoveAnubisKeysAsync(params string[] keys)
    {
        var section = await Settings.ReadSectionAsync(AnubisOptions.SectionName) ?? new JsonObject();
        foreach (var key in keys)
        {
            section.Remove(key);
        }

        await Settings.SaveSectionAsync(AnubisOptions.SectionName, (JsonObject)section.DeepClone());
    }
}
