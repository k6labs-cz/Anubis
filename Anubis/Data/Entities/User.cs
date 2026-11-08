using Microsoft.AspNetCore.Identity;

namespace k6labs.Anubis.Data.Entities;

/// <summary>An Anubis user. The email address doubles as the username.</summary>
public class User : IdentityUser
{
    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Preferred UI and email language (two-letter code, see Cultures); null = follow the browser.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(10)]
    public string? Language { get; set; }

    /// <summary>Preferred color mode: "light" or "dark"; null = follow the system setting.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(10)]
    public string? ColorScheme { get; set; }

    /// <summary>Custom avatar URL; when empty, a Gravatar derived from the email is used.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(500)]
    public string? AvatarUrl { get; set; }
}
