namespace k6labs.Anubis.Options;

/// <summary>
/// Password policy, lockout, sign-in session and passkey limits, bound to <c>Anubis:Security</c>
/// (edited on the admin "Security" page). Read through IOptionsMonitor, so changes apply
/// without a restart.
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Anubis:Security";

    public int PasswordMinLength { get; set; } = 12;

    public bool PasswordRequireDigit { get; set; } = true;

    public bool PasswordRequireLowercase { get; set; } = true;

    public bool PasswordRequireUppercase { get; set; } = true;

    public bool PasswordRequireNonAlphanumeric { get; set; }

    /// <summary>Failed sign-ins in a row that lock the account.</summary>
    public int LockoutMaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Lifetime of the sign-in cookie.</summary>
    public int SessionLifetimeHours { get; set; } = 8;

    /// <summary>Renew the sign-in cookie while the user is active.</summary>
    public bool SlidingSession { get; set; } = true;

    public int MaxPasskeysPerUser { get; set; } = 20;

    /// <summary>Allowed ranges (used by the admin page and when applying the values).</summary>
    public static class Limits
    {
        public const int PasswordMinLengthMin = 8;
        public const int PasswordMinLengthMax = 128;
        public const int LockoutAttemptsMin = 1;
        public const int LockoutAttemptsMax = 100;
        public const int LockoutMinutesMin = 1;
        public const int LockoutMinutesMax = 1440;
        public const int SessionHoursMin = 1;
        public const int SessionHoursMax = 720;
        public const int MaxPasskeysMin = 1;
        public const int MaxPasskeysMax = 100;
    }
}
