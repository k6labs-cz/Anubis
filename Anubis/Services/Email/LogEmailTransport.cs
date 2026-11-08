namespace k6labs.Anubis.Services.Email;

/// <summary>
/// Used when no mail server is configured: nothing is sent, the message is logged.
/// The body (with its one-time links) is logged only in Development.
/// </summary>
public sealed class LogEmailTransport : IEmailTransport
{
    private readonly ILogger<LogEmailTransport> _logger;
    private readonly IHostEnvironment _environment;

    public LogEmailTransport(ILogger<LogEmailTransport> logger, IHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    public string Name => global::k6labs.Anubis.Resources.Translations.Email_LogOnly;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (_environment.IsDevelopment())
        {
            _logger.LogWarning(
                "Email is not configured; not sending \"{Subject}\" to {To}. Body:\n{Body}",
                message.Subject, message.To, message.TextBody);
        }
        else
        {
            _logger.LogWarning(
                "Email is not configured (set Email:Smtp:Host or another Email:Provider); \"{Subject}\" to {To} was not sent.",
                message.Subject, message.To);
        }

        return Task.CompletedTask;
    }
}
