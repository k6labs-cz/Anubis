using k6labs.Anubis.Options;

using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Options;

using MimeKit;

namespace k6labs.Anubis.Services.Email;

/// <summary>Sends email through an SMTP server using MailKit.</summary>
public sealed class SmtpEmailTransport : IEmailTransport
{
    private readonly EmailOptions _options;

    public SmtpEmailTransport(IOptions<EmailOptions> options)
    {
        _options = options.Value;
    }

    public string Name => "SMTP";

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var smtp = _options.Smtp;
        var mime = BuildMimeMessage(_options, message);

        using var client = new SmtpClient { Timeout = smtp.TimeoutSeconds * 1000 };
        try
        {
            await client.ConnectAsync(smtp.Host!, smtp.Port, ToSocketOptions(smtp.Security), cancellationToken);

            if (!string.IsNullOrEmpty(smtp.Username))
            {
                await client.AuthenticateAsync(smtp.Username, smtp.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EmailDeliveryException($"SMTP delivery via {smtp.Host}:{smtp.Port} failed: {exception.Message}", exception);
        }
    }

    public static MimeMessage BuildMimeMessage(EmailOptions options, EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        return mime;
    }

    private static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto,
    };
}
