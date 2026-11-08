using System.Net.Mail;

using k6labs.Anubis.Options;

using Microsoft.Extensions.Options;

namespace k6labs.Anubis.Services.Email;

/// <summary>Fails startup with a clear message when the selected provider is missing settings.</summary>
public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        if (!options.IsConfigured)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();

        if (!MailAddress.TryCreate(options.FromAddress, out _))
        {
            errors.Add("Email:FromAddress must be a valid email address.");
        }

        switch (options.Provider)
        {
            case EmailProvider.Smtp:
                if (options.Smtp.Port is <= 0 or > 65535)
                {
                    errors.Add("Email:Smtp:Port must be between 1 and 65535.");
                }

                if (!string.IsNullOrEmpty(options.Smtp.Username) && string.IsNullOrEmpty(options.Smtp.Password))
                {
                    errors.Add("Email:Smtp:Password is required when Email:Smtp:Username is set.");
                }

                break;

            case EmailProvider.SendGrid:
                Require(options.SendGrid.ApiKey, "Email:SendGrid:ApiKey", errors);
                RequireHttps(options.SendGrid.BaseUrl, "Email:SendGrid:BaseUrl", errors);
                break;

            case EmailProvider.Mailgun:
                Require(options.Mailgun.ApiKey, "Email:Mailgun:ApiKey", errors);
                Require(options.Mailgun.Domain, "Email:Mailgun:Domain", errors);
                RequireHttps(options.Mailgun.BaseUrl, "Email:Mailgun:BaseUrl", errors);
                break;

            case EmailProvider.Postmark:
                Require(options.Postmark.ServerToken, "Email:Postmark:ServerToken", errors);
                RequireHttps(options.Postmark.BaseUrl, "Email:Postmark:BaseUrl", errors);
                break;

            case EmailProvider.Resend:
                Require(options.Resend.ApiKey, "Email:Resend:ApiKey", errors);
                RequireHttps(options.Resend.BaseUrl, "Email:Resend:BaseUrl", errors);
                break;
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void Require(string? value, string key, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{key} is required for the selected Email:Provider.");
        }
    }

    private static void RequireHttps(string value, string key, List<string> errors)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"{key} must be an absolute https:// URL.");
        }
    }
}
