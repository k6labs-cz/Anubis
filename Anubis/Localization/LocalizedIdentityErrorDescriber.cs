using Microsoft.AspNetCore.Identity;

namespace k6labs.Anubis.Localization;

/// <summary>Identity's validation errors (password rules, duplicate email, ...) in the user's language.</summary>
public sealed class LocalizedIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => Error(nameof(DefaultError), Translations.Identity_DefaultError);

    public override IdentityError ConcurrencyFailure() => Error(nameof(ConcurrencyFailure), Translations.Identity_ConcurrencyFailure);

    public override IdentityError PasswordMismatch() => Error(nameof(PasswordMismatch), Translations.Identity_PasswordMismatch);

    public override IdentityError InvalidToken() => Error(nameof(InvalidToken), Translations.Identity_InvalidToken);

    public override IdentityError RecoveryCodeRedemptionFailed() => Error(nameof(RecoveryCodeRedemptionFailed), Translations.Identity_RecoveryCodeRedemptionFailed);

    public override IdentityError LoginAlreadyAssociated() => Error(nameof(LoginAlreadyAssociated), Translations.Identity_LoginAlreadyAssociated);

    public override IdentityError InvalidUserName(string? userName) => Error(nameof(InvalidUserName), Translations.Identity_InvalidUserName.F(userName));

    public override IdentityError InvalidEmail(string? email) => Error(nameof(InvalidEmail), Translations.Identity_InvalidEmail.F(email));

    public override IdentityError DuplicateUserName(string userName) => Error(nameof(DuplicateUserName), Translations.Identity_DuplicateUserName.F(userName));

    public override IdentityError DuplicateEmail(string email) => Error(nameof(DuplicateEmail), Translations.Identity_DuplicateEmail.F(email));

    public override IdentityError InvalidRoleName(string? role) => Error(nameof(InvalidRoleName), Translations.Identity_InvalidRoleName.F(role));

    public override IdentityError DuplicateRoleName(string role) => Error(nameof(DuplicateRoleName), Translations.Identity_DuplicateRoleName.F(role));

    public override IdentityError UserAlreadyHasPassword() => Error(nameof(UserAlreadyHasPassword), Translations.Identity_UserAlreadyHasPassword);

    public override IdentityError UserLockoutNotEnabled() => Error(nameof(UserLockoutNotEnabled), Translations.Identity_UserLockoutNotEnabled);

    public override IdentityError UserAlreadyInRole(string role) => Error(nameof(UserAlreadyInRole), Translations.Identity_UserAlreadyInRole.F(role));

    public override IdentityError UserNotInRole(string role) => Error(nameof(UserNotInRole), Translations.Identity_UserNotInRole.F(role));

    public override IdentityError PasswordTooShort(int length) => Error(nameof(PasswordTooShort), Translations.Identity_PasswordTooShort.F(length));

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => Error(nameof(PasswordRequiresUniqueChars), Translations.Identity_PasswordRequiresUniqueChars.F(uniqueChars));

    public override IdentityError PasswordRequiresNonAlphanumeric() => Error(nameof(PasswordRequiresNonAlphanumeric), Translations.Identity_PasswordRequiresNonAlphanumeric);

    public override IdentityError PasswordRequiresDigit() => Error(nameof(PasswordRequiresDigit), Translations.Identity_PasswordRequiresDigit);

    public override IdentityError PasswordRequiresLower() => Error(nameof(PasswordRequiresLower), Translations.Identity_PasswordRequiresLower);

    public override IdentityError PasswordRequiresUpper() => Error(nameof(PasswordRequiresUpper), Translations.Identity_PasswordRequiresUpper);

    // Codes stay the same as the base class, so code that checks e.Code keeps working.
    private static IdentityError Error(string code, string description) => new() { Code = code, Description = description };
}
