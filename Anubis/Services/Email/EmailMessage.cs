namespace k6labs.Anubis.Services.Email;

/// <summary>A single outgoing email with HTML and plain-text bodies.</summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>Delivers an <see cref="EmailMessage"/> through one provider (SMTP, SendGrid, ...).</summary>
public interface IEmailTransport
{
    /// <summary>Provider name for logs and the admin page.</summary>
    public string Name { get; }

    /// <exception cref="EmailDeliveryException">The provider rejected the message or could not be reached.</exception>
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Sending failed. The message never contains credentials.</summary>
public sealed class EmailDeliveryException : Exception
{
    public EmailDeliveryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
