using System.Globalization;

using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Localization;

using Microsoft.AspNetCore.Identity;

namespace k6labs.Anubis.Services.Email;

/// <summary>
/// Identity's email sender. Each email is written in the recipient's saved language
/// (<see cref="User.Language"/>), or the current request's language.
/// </summary>
public sealed class AccountEmailSender : IEmailSender<User>
{
    private readonly IEmailTransport _transport;

    public AccountEmailSender(IEmailTransport transport)
    {
        _transport = transport;
    }

    public Task SendConfirmationLinkAsync(User user, string email, string confirmationLink)
        => _transport.SendAsync(Cultures.With(Cultures.ForUser(user.Language), () => EmailTemplates.Action(
            email,
            Translations.EmailConfirm_Subject,
            Translations.EmailConfirm_Subject,
            [Translations.EmailConfirm_Text.F(Name(user), email)],
            Translations.EmailConfirm_Button,
            confirmationLink,
            Translations.EmailConfirm_Footer)));

    public Task SendPasswordResetLinkAsync(User user, string email, string resetLink)
        => _transport.SendAsync(Cultures.With(Cultures.ForUser(user.Language), () => EmailTemplates.Action(
            email,
            Translations.EmailReset_Subject,
            Translations.EmailReset_Subject,
            [Translations.EmailReset_Text.F(Name(user))],
            Translations.EmailReset_Button,
            resetLink,
            Translations.EmailReset_Footer)));

    // Codes are not used (links only), but the interface requires the method.
    public Task SendPasswordResetCodeAsync(User user, string email, string resetCode)
        => throw new NotSupportedException("Anubis sends password reset links, not codes.");

    /// <summary>A test message for the admin page.</summary>
    public static EmailMessage TestMessage(string to, string transportName, string appUrl, CultureInfo culture)
        => Cultures.With(culture, () => EmailTemplates.Action(
            to,
            Translations.EmailTest_Subject,
            Translations.EmailTest_Heading,
            [Translations.EmailTest_Text.F(transportName)],
            Translations.EmailTest_Button,
            appUrl,
            Translations.EmailTest_Footer));

    private static string Name(User user) => user.DisplayName ?? user.Email ?? Translations.Email_GreetingFallback;
}
