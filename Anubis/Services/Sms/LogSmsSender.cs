namespace k6labs.Anubis.Services.Sms;

/// <summary>
/// Placeholder SMS gateway: writes the message to the log instead of sending it,
/// so phone verification can be tried out (the code is in the log).
/// </summary>
public sealed class LogSmsSender : ISmsSender
{
    private readonly ILogger<LogSmsSender> _logger;

    public LogSmsSender(ILogger<LogSmsSender> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("SMS is not configured; message to {PhoneNumber} only logged: {Message}", phoneNumber, message);
        return Task.CompletedTask;
    }
}
