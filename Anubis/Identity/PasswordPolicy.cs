using k6labs.Anubis.Options;

using Microsoft.Extensions.Options;

namespace k6labs.Anubis.Identity;

/// <summary>The current password rules (admin "Security" page), for hints such as "At least 12 characters".</summary>
public sealed class PasswordPolicy
{
    private readonly IOptionsMonitor<SecurityOptions> _security;

    public PasswordPolicy(IOptionsMonitor<SecurityOptions> security)
    {
        _security = security;
    }

    public int MinLength => Math.Clamp(_security.CurrentValue.PasswordMinLength, SecurityOptions.Limits.PasswordMinLengthMin, SecurityOptions.Limits.PasswordMinLengthMax);
}
