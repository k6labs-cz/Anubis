using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace k6labs.Anubis.Identity;

/// <summary>
/// Separate token provider for email confirmation, so its links can live longer (24 h)
/// than password reset links (1 h, default provider).
/// </summary>
public sealed class EmailConfirmationTokenProvider<TUser> : DataProtectorTokenProvider<TUser>
    where TUser : class
{
    public const string ProviderName = "EmailConfirmation";

    public EmailConfirmationTokenProvider(
        IDataProtectionProvider dataProtectionProvider,
        IOptions<EmailConfirmationTokenProviderOptions> options,
        ILogger<DataProtectorTokenProvider<TUser>> logger)
        : base(dataProtectionProvider, options, logger)
    {
    }
}

public sealed class EmailConfirmationTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public EmailConfirmationTokenProviderOptions()
    {
        Name = "EmailConfirmationDataProtectorTokenProvider";
        TokenLifespan = TimeSpan.FromHours(24);
    }
}
