namespace k6labs.Anubis.Options;

/// <summary>
/// Outgoing email, bound to the <c>Email</c> configuration section.
/// <see cref="Provider"/> picks the transport; only that provider's section is used.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary><c>Smtp</c> (default), <c>SendGrid</c>, <c>Mailgun</c>, <c>Postmark</c> or <c>Resend</c>.</summary>
    public EmailProvider Provider { get; set; } = EmailProvider.Smtp;

    /// <summary>Sender address. Must be allowed by the provider (verified domain/sender).</summary>
    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "Anubis";

    public SmtpEmailOptions Smtp { get; set; } = new();

    public SendGridEmailOptions SendGrid { get; set; } = new();

    public MailgunEmailOptions Mailgun { get; set; } = new();

    public PostmarkEmailOptions Postmark { get; set; } = new();

    public ResendEmailOptions Resend { get; set; } = new();

    /// <summary>
    /// False when SMTP is selected without a host: emails are then only written to the log.
    /// This keeps a fresh install and local development working without a mail server.
    /// </summary>
    public bool IsConfigured => Provider != EmailProvider.Smtp || !string.IsNullOrWhiteSpace(Smtp.Host);
}

public enum EmailProvider
{
    Smtp,
    SendGrid,
    Mailgun,
    Postmark,
    Resend,
}

public sealed class SmtpEmailOptions
{
    public string? Host { get; set; }

    /// <summary>587 for STARTTLS (default), 465 for implicit TLS, 25 for unencrypted relays.</summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// <c>Auto</c> (default; TLS on 465, STARTTLS when offered otherwise), <c>StartTls</c>
    /// (STARTTLS is required), <c>SslOnConnect</c> (implicit TLS) or <c>None</c>.
    /// </summary>
    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;

    /// <summary>Leave empty for servers without authentication.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
}

public enum SmtpSecurity
{
    Auto,
    StartTls,
    SslOnConnect,
    None,
}

public sealed class SendGridEmailOptions
{
    public string? ApiKey { get; set; }

    /// <summary>API base URL; use <c>https://api.eu.sendgrid.com</c> for EU data residency.</summary>
    public string BaseUrl { get; set; } = "https://api.sendgrid.com";
}

public sealed class MailgunEmailOptions
{
    public string? ApiKey { get; set; }

    /// <summary>Sending domain, e.g. <c>mg.example.com</c>.</summary>
    public string? Domain { get; set; }

    /// <summary>API base URL; use <c>https://api.eu.mailgun.net</c> for EU domains.</summary>
    public string BaseUrl { get; set; } = "https://api.mailgun.net";
}

public sealed class PostmarkEmailOptions
{
    public string? ServerToken { get; set; }

    public string MessageStream { get; set; } = "outbound";

    public string BaseUrl { get; set; } = "https://api.postmarkapp.com";
}

public sealed class ResendEmailOptions
{
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.resend.com";
}
