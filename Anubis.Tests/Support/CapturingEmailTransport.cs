using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using k6labs.Anubis.Services.Email;

namespace Anubis.Tests.Support;

/// <summary>Replaces the real transport in tests and keeps every message.</summary>
public sealed partial class CapturingEmailTransport : IEmailTransport
{
    private readonly ConcurrentQueue<EmailMessage> _messages = new();

    public string Name => "Test";

    public IEnumerable<EmailMessage> Messages => _messages;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public IReadOnlyList<EmailMessage> To(string address) => _messages.Where(m => m.To == address).ToList();

    /// <summary>The path + query of the link in the last email to <paramref name="address"/>.</summary>
    public string LastLinkTo(string address)
    {
        var message = To(address).LastOrDefault() ?? throw new Xunit.Sdk.XunitException($"No email was sent to {address}.");
        var url = LinkRegex().Match(message.TextBody).Groups[1].Value;
        return new Uri(url).PathAndQuery;
    }

    [GeneratedRegex(@": (https?://\S+)")]
    private static partial Regex LinkRegex();
}
