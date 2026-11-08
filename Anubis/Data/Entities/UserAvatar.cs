using System.ComponentModel.DataAnnotations;

namespace k6labs.Anubis.Data.Entities;

/// <summary>
/// Custom user avatar image stored in SQLite as a transparent WebP (<c>image/webp</c>)
/// resized to at most 1024x1024 pixels.
/// </summary>
public class UserAvatar
{
    [Key]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    public User? User { get; set; }

    public byte[] Data { get; set; } = [];

    [MaxLength(50)]
    public string ContentType { get; set; } = "image/webp";

    public int Width { get; set; }

    public int Height { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

