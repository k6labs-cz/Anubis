using System.Net.Mail;

using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Models;
using k6labs.Anubis.Services.Avatar;
using k6labs.Anubis.Services.Sms;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace k6labs.Anubis.Services;

/// <summary>Outcome of an admin create / edit: errors per field ("" = the whole form).</summary>
public sealed class UserAdministrationResult
{
    public Dictionary<string, string> Errors { get; } = new();

    public User? User { get; init; }

    /// <summary>Set when the email changed and a confirmation email went to the new address.</summary>
    public Email.AccountEmailResult? ConfirmationEmail { get; init; }

    public bool Succeeded => Errors.Count == 0;

    internal UserAdministrationResult Add(string field, string message)
    {
        Errors.TryAdd(field, message);
        return this;
    }
}

/// <summary>
/// Creating and editing accounts in the admin UI. Everything is validated first (email format,
/// length and uniqueness, display name, phone number, roles, "last administrator" and
/// "own Admin role" rules, concurrent changes) and then applied in one database transaction,
/// so an edit is saved completely or not at all.
/// </summary>
public sealed class UserAdministration
{
    public const int EmailMaxLength = 256;
    public const int DisplayNameMaxLength = 100;

    private readonly UserManager<User> _users;
    private readonly RoleManager<Role> _roles;
    private readonly AnubisDbContext _db;
    private readonly Email.AccountEmailService _emails;
    private readonly AvatarService _avatars;

    public UserAdministration(UserManager<User> users, RoleManager<Role> roles, AnubisDbContext db, Email.AccountEmailService emails, AvatarService avatars)
    {
        _users = users;
        _roles = roles;
        _db = db;
        _emails = emails;
        _avatars = avatars;
    }

    /// <summary>All role names, sorted.</summary>
    public Task<List<string>> RoleNamesAsync()
        => _roles.Roles.OrderBy(r => r.Name).Select(r => r.Name!).ToListAsync();

    public async Task<UserEditModel?> LoadAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null)
        {
            return null;
        }

        // The circuit's DbContext may hold an older copy: always edit the current row.
        await _db.Entry(user).ReloadAsync();
        return new UserEditModel
        {
            Id = user.Id,
            ConcurrencyStamp = user.ConcurrencyStamp,
            Email = user.Email ?? string.Empty,
            EmailConfirmed = user.EmailConfirmed,
            DisplayName = user.DisplayName,
            PhoneNumber = user.PhoneNumber,
            Roles = new HashSet<string>(await _users.GetRolesAsync(user), StringComparer.OrdinalIgnoreCase),
            Claims = CustomClaims.ToEntries(await _users.GetClaimsAsync(user)),
        };
    }

    public async Task<UserAdministrationResult> CreateAsync(CreateUserModel model)
    {
        var result = new UserAdministrationResult();
        var email = model.Email.Trim();
        await ValidateEmailAsync(result, email, userId: null);
        var roles = await ValidateRolesAsync(result, model.Roles);
        if (!result.Succeeded)
        {
            return result;
        }

        var user = new User
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,   // created by an admin
            DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? email : model.DisplayName.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var created = await _users.CreateAsync(user, model.Password);
        if (!created.Succeeded)
        {
            return AddIdentityErrors(result, created);
        }

        var current = await _users.GetRolesAsync(user);   // the very first account already got Admin
        var missing = roles.Where(role => !current.Contains(role, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0 && AddIdentityErrors(result, await _users.AddToRolesAsync(user, missing)) is { Succeeded: false })
        {
            return result;
        }

        await transaction.CommitAsync();
        return new UserAdministrationResult { User = user };
    }

    /// <param name="model">The validated user changes submitted by the administrator.</param>
    /// <param name="actingUserId">The signed-in administrator (may not remove their own Admin role).</param>
    /// <param name="requestBaseUri">For the link in the confirmation email when <c>Anubis:Issuer</c> is not set.</param>
    public async Task<UserAdministrationResult> UpdateAsync(UserEditModel model, string? actingUserId, string? requestBaseUri = null)
    {
        var result = new UserAdministrationResult();
        var user = await _users.FindByIdAsync(model.Id);
        if (user is null)
        {
            return result.Add(string.Empty, Translations.Common_UserNotFound);
        }

        await _db.Entry(user).ReloadAsync();
        if (model.ConcurrencyStamp is not null && model.ConcurrencyStamp != user.ConcurrencyStamp)
        {
            return result.Add(string.Empty, Translations.Identity_ConcurrencyFailure);
        }

        var email = model.Email.Trim();
        var emailChanged = !string.Equals(email, user.Email, StringComparison.Ordinal);
        if (emailChanged)
        {
            await ValidateEmailAsync(result, email, user.Id);
        }

        var displayName = string.IsNullOrWhiteSpace(model.DisplayName) ? email : model.DisplayName.Trim();
        if (displayName.Length > DisplayNameMaxLength)
        {
            result.Add(nameof(UserEditModel.DisplayName), Translations.Validation_DisplayNameTooLong);
        }

        string? phone = null;
        if (!string.IsNullOrWhiteSpace(model.PhoneNumber))
        {
            phone = PhoneNumberService.Normalize(model.PhoneNumber);
            if (phone is null)
            {
                result.Add(nameof(UserEditModel.PhoneNumber), Translations.Account_PhoneInvalid);
            }
        }

        var roles = await ValidateRolesAsync(result, model.Roles);
        var currentRoles = await _users.GetRolesAsync(user);
        var isAdmin = currentRoles.Contains(DatabaseInitializer.AdminRole, StringComparer.OrdinalIgnoreCase);
        if (isAdmin && !roles.Contains(DatabaseInitializer.AdminRole))
        {
            if (user.Id == actingUserId)
            {
                result.Add(nameof(UserEditModel.Roles), Translations.Users_CannotRemoveOwnAdmin);
            }
            else if ((await _users.GetUsersInRoleAsync(DatabaseInitializer.AdminRole)).Count <= 1)
            {
                result.Add(nameof(UserEditModel.Roles), Translations.Roles_LastAdmin);
            }
        }

        var claims = CustomClaims.Validate(model.Claims, result.Errors);
        if (model.Avatar == AvatarChange.Replace && model.AvatarImage is not { Length: > 0 })
        {
            result.Add(nameof(UserEditModel.Avatar), Translations.Account_AvatarInvalid);
        }

        if (!result.Succeeded)
        {
            return result;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();

        if (emailChanged)
        {
            // The email is also the sign-in name. SetEmailAsync clears "confirmed" and renews
            // the security stamp; the admin's choice below decides the final state.
            if (AddIdentityErrors(result, await _users.SetEmailAsync(user, email)) is { Succeeded: false }
                || AddIdentityErrors(result, await _users.SetUserNameAsync(user, email)) is { Succeeded: false })
            {
                return result;
            }
        }

        if (phone != user.PhoneNumber && AddIdentityErrors(result, await _users.SetPhoneNumberAsync(user, phone)) is { Succeeded: false })
        {
            return result;
        }

        user.DisplayName = displayName;
        user.EmailConfirmed = model.EmailConfirmed;
        if (AddIdentityErrors(result, await _users.UpdateAsync(user)) is { Succeeded: false })
        {
            return result;
        }

        var toRemove = currentRoles.Where(role => !roles.Contains(role)).ToList();
        var toAdd = roles.Where(role => !currentRoles.Contains(role, StringComparer.OrdinalIgnoreCase)).ToList();
        if (toRemove.Count > 0 && AddIdentityErrors(result, await _users.RemoveFromRolesAsync(user, toRemove)) is { Succeeded: false })
        {
            return result;
        }

        if (toAdd.Count > 0 && AddIdentityErrors(result, await _users.AddToRolesAsync(user, toAdd)) is { Succeeded: false })
        {
            return result;
        }

        // Own claims: only the difference is written (the claims of the roles are not stored on the user).
        var currentClaims = (await _users.GetClaimsAsync(user)).Where(claim => !CustomClaims.IsReserved(claim.Type)).ToList();
        var removedClaims = currentClaims.Where(c => !claims.Any(n => n.Type == c.Type && n.Value == c.Value)).ToList();
        var addedClaims = claims.Where(n => !currentClaims.Any(c => c.Type == n.Type && c.Value == n.Value)).ToList();
        if (removedClaims.Count > 0 && AddIdentityErrors(result, await _users.RemoveClaimsAsync(user, removedClaims)) is { Succeeded: false })
        {
            return result;
        }

        if (addedClaims.Count > 0 && AddIdentityErrors(result, await _users.AddClaimsAsync(user, addedClaims)) is { Succeeded: false })
        {
            return result;
        }

        try
        {
            switch (model.Avatar)
            {
                case AvatarChange.Replace:
                    await _avatars.SetAvatarAsync(user, model.AvatarImage!);
                    break;
                case AvatarChange.Remove:
                    await _avatars.RemoveAvatarAsync(user);
                    break;
            }
        }
        catch (InvalidDataException)
        {
            return result.Add(nameof(UserEditModel.Avatar), Translations.Account_AvatarInvalid);
        }

        if (toRemove.Count > 0 || toAdd.Count > 0 || removedClaims.Count > 0 || addedClaims.Count > 0)
        {
            // Sign-in cookies and tokens carry roles: make existing sessions pick up the change.
            await _users.UpdateSecurityStampAsync(user);
        }

        await transaction.CommitAsync();

        // A new address the admin did not mark as confirmed: the user confirms it by email.
        Email.AccountEmailResult? confirmation = null;
        if (emailChanged && !user.EmailConfirmed)
        {
            confirmation = await _emails.SendConfirmationAsync(user, requestBaseUri ?? "/");
        }

        return new UserAdministrationResult { User = user, ConfirmationEmail = confirmation };
    }

    private async Task ValidateEmailAsync(UserAdministrationResult result, string email, string? userId)
    {
        const string field = nameof(UserEditModel.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            result.Add(field, Translations.Validation_EmailRequired);
            return;
        }

        if (email.Length > EmailMaxLength
            || !MailAddress.TryCreate(email, out var parsed)
            || !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase)
            || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
        {
            result.Add(field, Translations.Validation_EmailInvalid);
            return;
        }

        // Unique as email and as sign-in name (Identity compares the normalized values).
        var byEmail = await _users.FindByEmailAsync(email);
        var byName = await _users.FindByNameAsync(email);
        if ((byEmail is not null && byEmail.Id != userId) || (byName is not null && byName.Id != userId))
        {
            result.Add(field, Translations.Users_EmailInUse);
        }
    }

    private async Task<HashSet<string>> ValidateRolesAsync(UserAdministrationResult result, IEnumerable<string> requested)
    {
        var existing = await RoleNamesAsync();
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DatabaseInitializer.UserRole };   // every account
        foreach (var role in requested)
        {
            var match = existing.FirstOrDefault(name => string.Equals(name, role, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                result.Add(nameof(UserEditModel.Roles), Translations.Identity_InvalidRoleName.Format(role));
            }
            else
            {
                roles.Add(match);
            }
        }

        return roles;
    }

    private static UserAdministrationResult AddIdentityErrors(UserAdministrationResult result, IdentityResult identity)
    {
        foreach (var error in identity.Errors)
        {
            var field = error.Code switch
            {
                nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)
                    or nameof(IdentityErrorDescriber.InvalidEmail) or nameof(IdentityErrorDescriber.InvalidUserName) => nameof(UserEditModel.Email),
                _ when error.Code.StartsWith("Password", StringComparison.Ordinal) => nameof(CreateUserModel.Password),
                _ => string.Empty,
            };
            result.Errors[field] = result.Errors.TryGetValue(field, out var existing) ? $"{existing} {error.Description}" : error.Description;
        }

        return result;
    }
}
