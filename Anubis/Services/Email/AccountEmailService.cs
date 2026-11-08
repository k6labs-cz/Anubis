using System.Text;

using k6labs.Anubis.Components.Shared;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Options;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace k6labs.Anubis.Services.Email;

public enum AccountEmailResult
{
    Sent,

    /// <summary>The same email went to this account a moment ago.</summary>
    Throttled,

    /// <summary>The provider failed; details are in the log.</summary>
    Failed,
}

/// <summary>Creates one-time links (password reset, email confirmation) and emails them.</summary>
public sealed class AccountEmailService
{
    private readonly UserManager<User> _userManager;
    private readonly IEmailSender<User> _emailSender;
    private readonly EmailThrottle _throttle;
    private readonly AnubisOptions _options;
    private readonly ILogger<AccountEmailService> _logger;

    public AccountEmailService(
        UserManager<User> userManager,
        IEmailSender<User> emailSender,
        EmailThrottle throttle,
        IOptionsSnapshot<AnubisOptions> options,
        ILogger<AccountEmailService> logger)
    {
        _userManager = userManager;
        _emailSender = emailSender;
        _throttle = throttle;
        _options = options.Value;
        _logger = logger;
    }

    /// <param name="user">The user whose email address is being confirmed.</param>
    /// <param name="requestBaseUri">Fallback for links when <c>Anubis:Issuer</c> is not set.</param>
    public async Task<AccountEmailResult> SendConfirmationAsync(User user, string requestBaseUri)
    {
        if (!_throttle.TryAcquire("confirm", user.Id))
        {
            return AccountEmailResult.Throttled;
        }

        var token = Encode(await _userManager.GenerateEmailConfirmationTokenAsync(user));
        var link = PublicUrl.Combine(_options.Issuer, requestBaseUri,
            $"/confirm-email?userId={Uri.EscapeDataString(user.Id)}&code={token}");

        return await SendAsync("confirmation", user, () => _emailSender.SendConfirmationLinkAsync(user, user.Email!, link));
    }

    public async Task<AccountEmailResult> SendPasswordResetAsync(User user, string requestBaseUri)
    {
        if (!_throttle.TryAcquire("reset", user.Id))
        {
            return AccountEmailResult.Throttled;
        }

        var token = Encode(await _userManager.GeneratePasswordResetTokenAsync(user));
        var link = PublicUrl.Combine(_options.Issuer, requestBaseUri,
            $"/reset-password?email={Uri.EscapeDataString(user.Email!)}&code={token}");

        return await SendAsync("password reset", user, () => _emailSender.SendPasswordResetLinkAsync(user, user.Email!, link));
    }

    /// <summary>Decodes a token from a link; null when it was mangled.</summary>
    public static string? Decode(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    private async Task<AccountEmailResult> SendAsync(string kind, User user, Func<Task> send)
    {
        try
        {
            await send();
            _logger.LogInformation("Sent {Kind} email to user {UserId}.", kind, user.Id);
            return AccountEmailResult.Sent;
        }
        catch (EmailDeliveryException exception)
        {
            _logger.LogError(exception, "Could not send {Kind} email to user {UserId}.", kind, user.Id);
            return AccountEmailResult.Failed;
        }
    }
}
