using System.Security.Claims;

using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Services.Activity;
using k6labs.Anubis.Telemetry;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace k6labs.Anubis.Identity;

/// <summary>
/// <see cref="SignInManager{TUser}"/> that records sign-ins and sign-outs in the account
/// history and the <c>anubis.sign_ins</c> metric, for every way of signing in
/// (password, passkey, authenticator app, recovery code). Cookie refreshes
/// (<see cref="SignInManager{TUser}.RefreshSignInAsync"/>) are not sign-ins and are not recorded.
/// </summary>
public sealed class SignInManager : SignInManager<User>
{
    // Set by the public sign-in methods; SignInWithClaimsAsync (the shared final step)
    // records the sign-in only when one of them started it.
    private string? _method;

    public SignInManager(
        UserManager<User> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<User> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<User>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<User> confirmation,
        IOptionsMonitor<IdentityOptions> currentOptions)
        : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
        // Current settings (e.g. "require a confirmed email") instead of the startup snapshot.
        Options = currentOptions.CurrentValue;
    }

    private AccountActivity Activity => Context.RequestServices.GetRequiredService<AccountActivity>();

    public override async Task<SignInResult> PasswordSignInAsync(User user, string password, bool isPersistent, bool lockoutOnFailure)
    {
        _method = UserEventTypes.Methods.Password;
        var result = await base.PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
        await RecordOutcomeAsync(user, UserEventTypes.Methods.Password, result);
        return result;
    }

    public override async Task<SignInResult> PasskeySignInAsync(string credentialJson)
    {
        _method = UserEventTypes.Methods.Passkey;
        var result = await base.PasskeySignInAsync(credentialJson);
        if (!result.Succeeded)
        {
            // Unknown passkey: there is no account to put it on, only the metric.
            Count(UserEventTypes.Methods.Passkey, result);
        }

        return result;
    }

    public override async Task<SignInResult> TwoFactorAuthenticatorSignInAsync(string code, bool isPersistent, bool rememberClient)
    {
        _method = UserEventTypes.Methods.Authenticator;
        var user = await GetTwoFactorAuthenticationUserAsync();
        var result = await base.TwoFactorAuthenticatorSignInAsync(code, isPersistent, rememberClient);
        await RecordOutcomeAsync(user, UserEventTypes.Methods.Authenticator, result);
        return result;
    }

    public override async Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode)
    {
        _method = UserEventTypes.Methods.RecoveryCode;
        var user = await GetTwoFactorAuthenticationUserAsync();
        var result = await base.TwoFactorRecoveryCodeSignInAsync(recoveryCode);
        await RecordOutcomeAsync(user, UserEventTypes.Methods.RecoveryCode, result);
        return result;
    }

    public override async Task SignInWithClaimsAsync(User user, AuthenticationProperties? authenticationProperties, IEnumerable<Claim> additionalClaims)
    {
        await base.SignInWithClaimsAsync(user, authenticationProperties, additionalClaims);

        if (_method is { } method)
        {
            _method = null;
            Count(method, SignInResult.Success);
            await Activity.RecordAsync(user.Id, UserEventTypes.SignedIn, method);
        }
    }

    public override async Task SignOutAsync()
    {
        var userId = UserManager.GetUserId(Context.User);
        await base.SignOutAsync();
        if (userId is not null)
        {
            await Activity.RecordAsync(userId, UserEventTypes.SignedOut);
        }
    }

    private async Task RecordOutcomeAsync(User? user, string method, SignInResult result)
    {
        if (result.Succeeded)
        {
            return; // recorded by SignInWithClaimsAsync
        }

        _method = null;
        Count(method, result);
        if (user is null || result.RequiresTwoFactor)
        {
            return; // the second step decides
        }

        // Lockouts caused by this attempt are recorded by UserManager.AccessFailedAsync.
        if (!result.IsLockedOut)
        {
            await Activity.RecordAsync(user.Id, UserEventTypes.SignInFailed, method);
        }
    }

    private static void Count(string method, SignInResult result)
    {
        var outcome = result.Succeeded ? "success"
            : result.RequiresTwoFactor ? "requires_two_factor"
            : result.IsLockedOut ? "locked_out"
            : result.IsNotAllowed ? "not_allowed"
            : "failure";
        AnubisTelemetry.SignIns.Add(1,
            new KeyValuePair<string, object?>("anubis.sign_in.method", method),
            new KeyValuePair<string, object?>("anubis.sign_in.result", outcome));
    }
}
