using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Services.Activity;
using k6labs.Anubis.Telemetry;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace k6labs.Anubis.Identity;

/// <summary>
/// Custom <see cref="UserManager{TUser}"/>: the very first account gets the
/// <see cref="DatabaseInitializer.AdminGroup"/> group, and every security-relevant change
/// (password, MFA, passkeys, phone, email, lockout) is written to the account history,
/// whichever page or endpoint made it.
/// </summary>
public sealed class UserManager : UserManager<User>
{
    private readonly IServiceProvider _services;

    public UserManager(
        IUserStore<User> store,
        IOptions<IdentityOptions> optionsAccessor,
        IPasswordHasher<User> passwordHasher,
        IEnumerable<IUserValidator<User>> userValidators,
        IEnumerable<IPasswordValidator<User>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<UserManager<User>> logger)
        : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
    {
        _services = services;
        // Current policy (the admin "Security" page changes it without a restart).
        Options = services.GetRequiredService<IOptionsMonitor<IdentityOptions>>().CurrentValue;
    }

    // Resolved lazily: the history is written in the same scope (and DbContext) as the change.
    private Task RecordAsync(User user, string type, string? detail = null)
        => _services.GetRequiredService<AccountActivity>().RecordAsync(user.Id, type, detail);

    public override async Task<IdentityResult> CreateAsync(User user)
    {
        var isFirstUser = !await Users.AnyAsync();
        var result = await base.CreateAsync(user);
        if (result.Succeeded)
        {
            // An account created during a signed-in request was made by an admin; without a
            // request at all (no client address) it was seeded at startup.
            var client = _services.GetRequiredService<ClientInfo>();
            var source = client.UserId is not null ? "admin" : client.IpAddress is null ? "seed" : "self-service";
            AnubisTelemetry.Registrations.Add(1, new KeyValuePair<string, object?>("anubis.registration.source", source));
            await RecordAsync(user, UserEventTypes.AccountCreated);
        }

        if (result.Succeeded && isFirstUser)
        {
            if (!await IsInRoleAsync(user, DatabaseInitializer.UserGroup))
            {
                await AddToRoleAsync(user, DatabaseInitializer.UserGroup);
            }

            if (!await IsInRoleAsync(user, DatabaseInitializer.AdminGroup))
            {
                await AddToRoleAsync(user, DatabaseInitializer.AdminGroup);
            }
        }

        return result;
    }

    public override async Task<IdentityResult> ChangePasswordAsync(User user, string currentPassword, string newPassword)
        => await RecordIfSucceeded(await base.ChangePasswordAsync(user, currentPassword, newPassword), user, UserEventTypes.PasswordChanged);

    public override async Task<IdentityResult> ResetPasswordAsync(User user, string token, string newPassword)
        => await RecordIfSucceeded(await base.ResetPasswordAsync(user, token, newPassword), user, UserEventTypes.PasswordReset);

    public override async Task<IdentityResult> ConfirmEmailAsync(User user, string token)
    {
        var wasConfirmed = user.EmailConfirmed;
        var result = await base.ConfirmEmailAsync(user, token);
        return wasConfirmed ? result : await RecordIfSucceeded(result, user, UserEventTypes.EmailConfirmed);
    }

    public override async Task<IdentityResult> SetTwoFactorEnabledAsync(User user, bool enabled)
    {
        var wasEnabled = user.TwoFactorEnabled;
        var result = await base.SetTwoFactorEnabledAsync(user, enabled);
        return wasEnabled == enabled
            ? result
            : await RecordIfSucceeded(result, user, enabled ? UserEventTypes.TwoFactorEnabled : UserEventTypes.TwoFactorDisabled);
    }

    public override async Task<IEnumerable<string>?> GenerateNewTwoFactorRecoveryCodesAsync(User user, int number)
    {
        var codes = await base.GenerateNewTwoFactorRecoveryCodesAsync(user, number);
        if (codes is not null)
        {
            await RecordAsync(user, UserEventTypes.RecoveryCodesGenerated);
        }

        return codes;
    }

    public override async Task<IdentityResult> AddOrUpdatePasskeyAsync(User user, UserPasskeyInfo passkey)
    {
        var isNew = await GetPasskeyAsync(user, passkey.CredentialId) is null;
        var result = await base.AddOrUpdatePasskeyAsync(user, passkey);
        return isNew ? await RecordIfSucceeded(result, user, UserEventTypes.PasskeyAdded, passkey.Name) : result;
    }

    public override async Task<IdentityResult> RemovePasskeyAsync(User user, byte[] credentialId)
    {
        var name = (await GetPasskeyAsync(user, credentialId))?.Name;
        return await RecordIfSucceeded(await base.RemovePasskeyAsync(user, credentialId), user, UserEventTypes.PasskeyRemoved, name);
    }

    public override async Task<IdentityResult> SetPhoneNumberAsync(User user, string? phoneNumber)
    {
        var previous = user.PhoneNumber;
        var result = await base.SetPhoneNumberAsync(user, phoneNumber);
        if (previous == phoneNumber)
        {
            return result;
        }

        return string.IsNullOrEmpty(phoneNumber)
            ? await RecordIfSucceeded(result, user, UserEventTypes.PhoneRemoved, MaskPhone(previous))
            : await RecordIfSucceeded(result, user, UserEventTypes.PhoneChanged, MaskPhone(phoneNumber));
    }

    public override async Task<IdentityResult> ChangePhoneNumberAsync(User user, string phoneNumber, string token)
        => await RecordIfSucceeded(await base.ChangePhoneNumberAsync(user, phoneNumber, token), user, UserEventTypes.PhoneVerified, MaskPhone(phoneNumber));

    public override async Task<IdentityResult> SetLockoutEndDateAsync(User user, DateTimeOffset? lockoutEnd)
    {
        var wasLocked = user.LockoutEnd is { } end && end > DateTimeOffset.UtcNow;
        var locking = lockoutEnd is { } next && next > DateTimeOffset.UtcNow;
        var result = await base.SetLockoutEndDateAsync(user, lockoutEnd);
        return wasLocked == locking
            ? result
            : await RecordIfSucceeded(result, user, locking ? UserEventTypes.AccountLocked : UserEventTypes.AccountUnlocked);
    }

    public override async Task<IdentityResult> SetEmailAsync(User user, string? email)
    {
        var previous = user.Email;
        var result = await base.SetEmailAsync(user, email);
        return previous == email ? result : await RecordIfSucceeded(result, user, UserEventTypes.EmailChanged, email);
    }

    // Group changes (admin edit, group members). The built-in "Users" group every account gets is not recorded.
    public override async Task<IdentityResult> AddToRoleAsync(User user, string group)
        => await RecordGroupsIfSucceeded(await base.AddToRoleAsync(user, group), user, UserEventTypes.GroupAdded, [group]);

    public override async Task<IdentityResult> AddToRolesAsync(User user, IEnumerable<string> groups)
    {
        var list = groups.ToList();
        return await RecordGroupsIfSucceeded(await base.AddToRolesAsync(user, list), user, UserEventTypes.GroupAdded, list);
    }

    public override async Task<IdentityResult> RemoveFromRoleAsync(User user, string group)
        => await RecordGroupsIfSucceeded(await base.RemoveFromRoleAsync(user, group), user, UserEventTypes.GroupRemoved, [group]);

    public override async Task<IdentityResult> RemoveFromRolesAsync(User user, IEnumerable<string> groups)
    {
        var list = groups.ToList();
        return await RecordGroupsIfSucceeded(await base.RemoveFromRolesAsync(user, list), user, UserEventTypes.GroupRemoved, list);
    }

    private async Task<IdentityResult> RecordGroupsIfSucceeded(IdentityResult result, User user, string type, IEnumerable<string> groups)
    {
        if (result.Succeeded)
        {
            foreach (var group in groups.Where(r => !string.Equals(r, DatabaseInitializer.UserGroup, StringComparison.OrdinalIgnoreCase)))
            {
                await RecordAsync(user, type, group);
            }
        }

        return result;
    }

    /// <summary>A failed sign-in that reaches the attempt limit locks the account: record that.</summary>
    public override async Task<IdentityResult> AccessFailedAsync(User user)
    {
        var wasLocked = await IsLockedOutAsync(user);
        var result = await base.AccessFailedAsync(user);
        if (result.Succeeded && !wasLocked && await IsLockedOutAsync(user))
        {
            await RecordAsync(user, UserEventTypes.LockedOut);
        }

        return result;
    }

    /// <summary>"+420123456789" -> "+420 ••• ••• 789": enough to recognize the number, not to read it.</summary>
    public static string? MaskPhone(string? phoneNumber)
        => phoneNumber is { Length: > 7 } number ? $"{number[..4]} ••• ••• {number[^3..]}" : phoneNumber;

    private async Task<IdentityResult> RecordIfSucceeded(IdentityResult result, User user, string type, string? detail = null)
    {
        if (result.Succeeded)
        {
            await RecordAsync(user, type, detail);
        }

        return result;
    }
}
