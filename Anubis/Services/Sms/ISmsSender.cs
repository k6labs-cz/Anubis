namespace k6labs.Anubis.Services.Sms;

/// <summary>Sends a text message. Replace <see cref="LogSmsSender"/> with a real gateway in production.</summary>
public interface ISmsSender
{
    public Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default);
}
