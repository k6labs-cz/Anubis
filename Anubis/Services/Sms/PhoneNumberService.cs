using System.Text.RegularExpressions;

using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Localization;
using k6labs.Anubis.Services.Email;

using Microsoft.AspNetCore.Identity;

namespace k6labs.Anubis.Services.Sms;

public enum PhoneCodeResult
{
    Sent,
    InvalidNumber,
    Throttled,
}

/// <summary>
/// Phone number on an account. Saving a number stores it as not verified (also when it
/// changes); verifying sends a 6-digit code by SMS and marks the number as verified once
/// the code is entered. Uses Identity's phone token provider, so the code is bound to
/// both the account and the number.
/// </summary>
public sealed partial class PhoneNumberService
{
    public static readonly TimeSpan ResendInterval = TimeSpan.FromMinutes(1);

    private readonly UserManager<User> _userManager;
    private readonly ISmsSender _sms;
    private readonly EmailThrottle _throttle;

    public PhoneNumberService(UserManager<User> userManager, ISmsSender sms, EmailThrottle throttle)
    {
        _userManager = userManager;
        _sms = sms;
        _throttle = throttle;
    }

    /// <summary>
    /// Normalizes "+420 123 456 789", "+420-123-456-789" or "00420123456789" to E.164
    /// ("+420123456789"); null when it isn't an international number.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var compact = SeparatorsRegex().Replace(input.Trim(), string.Empty);
        if (compact.StartsWith("00", StringComparison.Ordinal))
        {
            compact = "+" + compact[2..];
        }

        return E164Regex().IsMatch(compact) ? compact : null;
    }

    /// <summary>
    /// Saves the number as not verified (an empty value removes it). A different number
    /// always needs verifying again; the same number keeps its state. False when invalid.
    /// </summary>
    public async Task<bool> SaveAsync(User user, string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            await RemoveAsync(user);
            return true;
        }

        if (Normalize(phoneNumber) is not { } number)
        {
            return false;
        }

        if (number != user.PhoneNumber)
        {
            // Identity's SetPhoneNumberAsync also clears PhoneNumberConfirmed.
            await _userManager.SetPhoneNumberAsync(user, number);
        }

        return true;
    }

    public async Task<PhoneCodeResult> SendCodeAsync(User user, string? phoneNumber, CancellationToken cancellationToken = default)
    {
        if (Normalize(phoneNumber) is not { } number)
        {
            return PhoneCodeResult.InvalidNumber;
        }

        if (!_throttle.TryAcquire("phone-code", user.Id, ResendInterval))
        {
            return PhoneCodeResult.Throttled;
        }

        var code = await _userManager.GenerateChangePhoneNumberTokenAsync(user, number);
        var message = Cultures.With(Cultures.ForUser(user.Language), () => Translations.Sms_VerificationCode.Format(code));

        using var activity = Telemetry.AnubisTelemetry.ActivitySource.StartActivity("sms.send", System.Diagnostics.ActivityKind.Client);
        try
        {
            await _sms.SendAsync(number, message, cancellationToken);
            Telemetry.AnubisTelemetry.SmsSent.Add(1, new KeyValuePair<string, object?>("anubis.result", "success"));
        }
        catch (Exception exception)
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.Message);
            Telemetry.AnubisTelemetry.SmsSent.Add(1, new KeyValuePair<string, object?>("anubis.result", "failure"));
            throw;
        }

        return PhoneCodeResult.Sent;
    }

    /// <summary>Marks the number as verified (and saves it) if the code matches.</summary>
    public async Task<bool> ConfirmAsync(User user, string? phoneNumber, string? code)
    {
        if (Normalize(phoneNumber) is not { } number || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var result = await _userManager.ChangePhoneNumberAsync(user, number, code.Trim().Replace(" ", string.Empty));
        return result.Succeeded;
    }

    public async Task RemoveAsync(User user)
    {
        await _userManager.SetPhoneNumberAsync(user, null);
    }

    [GeneratedRegex(@"[\s\-().]")]
    private static partial Regex SeparatorsRegex();

    [GeneratedRegex(@"^\+[1-9][0-9]{6,14}$")]
    private static partial Regex E164Regex();
}
