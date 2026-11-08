using Microsoft.Extensions.Caching.Memory;

namespace k6labs.Anubis.Services.Email;

/// <summary>
/// Limits how often the same kind of email goes to the same account, so the public
/// "forgot password" and "resend confirmation" forms can't be used to flood an inbox.
/// In-memory, so the limit is per app instance.
/// </summary>
public sealed class EmailThrottle
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(2);

    private readonly IMemoryCache _cache;

    public EmailThrottle(IMemoryCache cache)
    {
        _cache = cache;
    }

    /// <summary>True if sending is allowed now; then blocks the same key for <paramref name="interval"/>.</summary>
    public bool TryAcquire(string purpose, string userId, TimeSpan? interval = null)
    {
        var key = $"email-throttle:{purpose}:{userId}";
        if (_cache.TryGetValue(key, out _))
        {
            return false;
        }

        _cache.Set(key, true, interval ?? DefaultInterval);
        return true;
    }
}
