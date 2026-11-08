using System.Diagnostics;

using k6labs.Anubis.Telemetry;

namespace k6labs.Anubis.Services.Email;

/// <summary>Wraps the configured transport with an OpenTelemetry span, a counter and a duration histogram.</summary>
public sealed class InstrumentedEmailTransport : IEmailTransport
{
    private readonly IEmailTransport _inner;

    public InstrumentedEmailTransport(IEmailTransport inner)
    {
        _inner = inner;
    }

    public string Name => _inner.Name;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var activity = AnubisTelemetry.ActivitySource.StartActivity("email.send", ActivityKind.Client);
        activity?.SetTag("anubis.email.provider", _inner.Name);

        var started = Stopwatch.GetTimestamp();
        var result = "success";
        try
        {
            await _inner.SendAsync(message, cancellationToken);
        }
        catch (Exception exception)
        {
            result = "failure";
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
        finally
        {
            var tags = new TagList { { "anubis.email.provider", _inner.Name }, { "anubis.result", result } };
            AnubisTelemetry.EmailsSent.Add(1, tags);
            AnubisTelemetry.EmailSendDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }
}
