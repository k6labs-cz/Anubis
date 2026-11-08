using System.ComponentModel.DataAnnotations;

namespace k6labs.Anubis.Data.Entities;

/// <summary>One entry of an account's history (shown on /account/history).</summary>
public class UserEvent
{
    public long Id { get; set; }

    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    public User? User { get; set; }

    /// <summary>UTC time (DateTime, so SQLite can sort and filter it).</summary>
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>One of <see cref="Anubis.Services.Activity.UserEventTypes"/>.</summary>
    [MaxLength(64)]
    public string Type { get; set; } = string.Empty;

    /// <summary>Extra info, e.g. the sign-in method, a passkey name or an application name.</summary>
    [MaxLength(256)]
    public string? Detail { get; set; }

    /// <summary>Set when someone else (an administrator) did it.</summary>
    [MaxLength(450)]
    public string? ActorUserId { get; set; }

    [MaxLength(45)]
    public string? IpAddress { get; set; }

    [MaxLength(512)]
    public string? UserAgent { get; set; }
}
