using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Telemetry;

namespace k6labs.Anubis.Services.Activity;

/// <summary>
/// Writes account history events (with IP address and user agent), counts them in the
/// <c>anubis.account.events</c> metric and logs them. Never throws: a failed history entry
/// must not break the action it describes.
/// </summary>
public sealed class AccountActivity
{
    private readonly AnubisDbContext _db;
    private readonly ClientInfo _client;
    private readonly ILogger<AccountActivity> _logger;

    public AccountActivity(AnubisDbContext db, ClientInfo client, ILogger<AccountActivity> logger)
    {
        _db = db;
        _client = client;
        _logger = logger;
    }

    public async Task RecordAsync(string userId, string type, string? detail = null, CancellationToken cancellationToken = default)
    {
        AnubisTelemetry.AccountEvents.Add(1, new KeyValuePair<string, object?>("anubis.account_event.type", type));
        var actor = _client.UserId is { } id && id != userId ? id : null;
        _logger.LogInformation(
            "Account event {EventType} for user {UserId} (detail {Detail}, by {ActorUserId}, from {IpAddress}).",
            type, userId, detail, actor ?? userId, _client.IpAddress);

        try
        {
            _db.UserEvents.Add(new UserEvent
            {
                UserId = userId,
                OccurredAtUtc = DateTime.UtcNow,
                Type = type,
                Detail = detail is { Length: > 256 } ? detail[..256] : detail,
                ActorUserId = actor,
                IpAddress = _client.IpAddress,
                UserAgent = _client.UserAgent,
            });
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not record account event {EventType} for user {UserId}.", type, userId);
        }
    }
}
