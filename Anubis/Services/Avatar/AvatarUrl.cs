using System.Security.Cryptography;
using System.Text;

using k6labs.Anubis.Data.Entities;

namespace k6labs.Anubis.Services.Avatar;

/// <summary>
/// Avatar URL of a user: their custom <see cref="User.AvatarUrl"/> when set,
/// otherwise a Gravatar derived from the email address (MD5, per the Gravatar spec).
/// </summary>
public static class AvatarUrl
{
    public static string For(User user, int size = 64)
        => !string.IsNullOrWhiteSpace(user.AvatarUrl)
            ? user.AvatarUrl
            : ForEmail(user.Email, size);

    public static string ForEmail(string? email, int size = 64)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        // d=identicon: Gravatar generates a unique default image when the user has none.
        return $"https://www.gravatar.com/avatar/{hash}?s={size}&d=identicon";
    }
}
