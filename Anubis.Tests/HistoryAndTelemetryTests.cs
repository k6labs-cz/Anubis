using System.Diagnostics;
using System.Diagnostics.Metrics;

using Anubis.Tests.Support;

using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Services.Activity;
using k6labs.Anubis.Telemetry;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Anubis.Tests;

/// <summary>Account history (/history) and the OpenTelemetry signals.</summary>
[Collection(AnubisCollection.Name)]
public class HistoryAndTelemetryTests
{
    private readonly AnubisFactory _factory;

    public HistoryAndTelemetryTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SignIns_FailedAttempts_AndSignOut_AreRecorded_AndShownOnTheHistoryPage()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();

        (await client.PasswordLoginAsync(user.Email!, "wrong-password-123!")).Dispose();
        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();

        var html = await client.Http.GetStringAsync("/history");
        Assert.Contains("Signed in", html);
        Assert.Contains("Failed sign-in attempt", html);
        Assert.Contains("password", html);
        Assert.Contains("data-local-time", html);
        Assert.Contains("href=\"/history\"", await client.Http.GetStringAsync("/profile"));   // navigation

        await client.SignOutAsync();

        var types = await EventTypesAsync(user.Id);
        Assert.Equal([UserEventTypes.AccountCreated, UserEventTypes.SignInFailed, UserEventTypes.SignedIn, UserEventTypes.SignedOut], types);
    }

    [Fact]
    public async Task SecurityChanges_AreRecorded_WithoutCookieRefreshesCountingAsSignIns()
    {
        using var client = new FormClient(_factory);
        var (created, password) = await client.CreateUserAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = (await userManager.FindByIdAsync(created.Id))!;
            await userManager.ChangePasswordAsync(user, password, password + "x");
            await userManager.SetTwoFactorEnabledAsync(user, true);
            await userManager.SetTwoFactorEnabledAsync(user, true);              // no change, no entry
            await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            await userManager.SetPhoneNumberAsync(user, "+420123456789");
            await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddYears(1));
            await userManager.SetLockoutEndDateAsync(user, null);
        }

        var types = await EventTypesAsync(created.Id);
        Assert.Equal(
        [
            UserEventTypes.AccountCreated, UserEventTypes.PasswordChanged, UserEventTypes.TwoFactorEnabled,
            UserEventTypes.RecoveryCodesGenerated, UserEventTypes.PhoneChanged, UserEventTypes.AccountLocked, UserEventTypes.AccountUnlocked,
        ], types);

        using var verify = _factory.Services.CreateScope();
        var phone = await verify.ServiceProvider.GetRequiredService<AnubisDbContext>().UserEvents
            .SingleAsync(e => e.UserId == created.Id && e.Type == UserEventTypes.PhoneChanged);
        Assert.Equal("+420 ••• ••• 789", phone.Detail);   // never the full number
    }

    [Fact]
    public async Task FailedAttempts_ThatLockTheAccount_AreRecordedAsLockout()
    {
        using var client = new FormClient(_factory);
        var (user, _) = await client.CreateUserAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            (await client.PasswordLoginAsync(user.Email!, "wrong-password-123!")).Dispose();
        }

        var types = await EventTypesAsync(user.Id);
        Assert.Equal(4, types.Count(t => t == UserEventTypes.SignInFailed));
        Assert.Equal(UserEventTypes.LockedOut, types[^1]);
    }

    [Fact]
    public async Task HistoryPage_IsPaged_AndShowsOnlyTheOwnEntries()
    {
        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        var (other, _) = await client.CreateUserAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnubisDbContext>();
            for (var i = 0; i < 30; i++)
            {
                db.UserEvents.Add(new UserEvent { UserId = user.Id, Type = UserEventTypes.AvatarChanged, UserAgent = "Mozilla/5.0 (Windows NT 10.0) Firefox/140.0" });
            }

            db.UserEvents.Add(new UserEvent { UserId = other.Id, Type = UserEventTypes.PasskeyAdded, Detail = "someone-elses-passkey" });
            await db.SaveChangesAsync();
        }

        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();

        var first = await client.Http.GetStringAsync("/history");
        Assert.Equal(25, System.Text.RegularExpressions.Regex.Matches(first, "data-history-entry=").Count);
        Assert.Contains("href=\"/history?page=2\"", first);
        Assert.Contains("Firefox · Windows", first);
        Assert.DoesNotContain("someone-elses-passkey", first);

        var second = await client.Http.GetStringAsync("/history?page=2");
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(second, "data-history-entry=").Count);   // 30 + created + signed in - 25
        Assert.Contains("href=\"/history\"", second);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36 Edg/140.0", "Edge · Windows")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "Safari · iOS")]
    [InlineData("Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Mobile Safari/537.36", "Chrome · Android")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0; rv:140.0) Gecko/20100101 Firefox/140.0", "Firefox · macOS")]
    [InlineData("curl/8.5.0", null)]
    [InlineData(null, null)]
    public void UserAgents_AreDescribedShortly(string? userAgent, string? expected)
    {
        Assert.Equal(expected, UserAgentInfo.Describe(userAgent));
    }

    [Fact]
    public async Task SignIns_AreCounted_AndPasswordChecksAreTraced()
    {
        var signIns = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var meters = new MeterListener();
        meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AnubisTelemetry.Name && instrument.Name == "anubis.sign_ins")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meters.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            lock (signIns)
            {
                signIns.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
            }
        });
        meters.Start();

        var spans = new List<string>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AnubisTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (spans) { spans.Add(activity.OperationName); } },
        };
        ActivitySource.AddActivityListener(activities);

        using var client = new FormClient(_factory);
        var (user, password) = await client.CreateUserAsync();
        (await client.PasswordLoginAsync(user.Email!, password)).Dispose();

        lock (signIns)
        {
            Assert.Contains(signIns, m => Equals(m.Tags["anubis.sign_in.method"], "password") && Equals(m.Tags["anubis.sign_in.result"], "success"));
        }

        lock (spans)
        {
            Assert.Contains("password.verify", spans);
        }
    }

    [Fact]
    public void OpenTelemetry_IsRegistered_ForTracesAndMetrics()
    {
        Assert.NotNull(_factory.Services.GetService<TracerProvider>());
        Assert.NotNull(_factory.Services.GetService<MeterProvider>());
    }

    private async Task<List<string>> EventTypesAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AnubisDbContext>().UserEvents
            .Where(e => e.UserId == userId)
            .OrderBy(e => e.Id)
            .Select(e => e.Type)
            .ToListAsync();
    }
}
