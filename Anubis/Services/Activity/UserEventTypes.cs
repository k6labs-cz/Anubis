namespace k6labs.Anubis.Services.Activity;

/// <summary>Event types of the account history (<see cref="Data.Entities.UserEvent.Type"/>).</summary>
public static class UserEventTypes
{
    public const string AccountCreated = "account.created";
    public const string SignedIn = "sign_in.succeeded";
    public const string SignInFailed = "sign_in.failed";
    public const string LockedOut = "sign_in.locked_out";
    public const string SignedOut = "sign_out";
    public const string ApplicationSignIn = "application.sign_in";
    public const string PasswordChanged = "password.changed";
    public const string PasswordReset = "password.reset";
    public const string EmailConfirmed = "email.confirmed";
    public const string EmailChanged = "email.changed";
    public const string RoleAdded = "role.added";
    public const string RoleRemoved = "role.removed";
    public const string TwoFactorEnabled = "two_factor.enabled";
    public const string TwoFactorDisabled = "two_factor.disabled";
    public const string RecoveryCodesGenerated = "recovery_codes.generated";
    public const string PasskeyAdded = "passkey.added";
    public const string PasskeyRemoved = "passkey.removed";
    public const string PhoneChanged = "phone.changed";
    public const string PhoneVerified = "phone.verified";
    public const string PhoneRemoved = "phone.removed";
    public const string ProfileUpdated = "profile.updated";
    public const string AvatarChanged = "avatar.changed";
    public const string AccountLocked = "account.locked";
    public const string AccountUnlocked = "account.unlocked";

    /// <summary>Sign-in methods stored in <see cref="Data.Entities.UserEvent.Detail"/> of sign-in events.</summary>
    public static class Methods
    {
        public const string Password = "password";
        public const string Passkey = "passkey";
        public const string Authenticator = "authenticator";
        public const string RecoveryCode = "recovery-code";
    }
}
