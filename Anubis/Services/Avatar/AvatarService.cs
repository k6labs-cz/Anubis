using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace k6labs.Anubis.Services.Avatar;

/// <summary>
/// Resizes uploaded user avatar images (raster formats or SVG) to at most 1024x1024 pixels,
/// encodes them as transparent WebP (<c>image/webp</c>), and stores them in the SQLite
/// <c>UserAvatars</c> table.
/// </summary>
public sealed class AvatarService
{
    public const int MaxDimension = 1024;
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    public const string WebpContentType = ImageProcessing.WebpContentType;

    private readonly AnubisDbContext _db;
    private readonly UserManager<User> _userManager;
    private readonly Activity.AccountActivity _activity;

    public AvatarService(AnubisDbContext db, UserManager<User> userManager, Activity.AccountActivity activity)
    {
        _db = db;
        _userManager = userManager;
        _activity = activity;
    }

    /// <summary>
    /// Accepts JPEG, PNG, WebP, GIF, BMP and SVG. SVGs are rasterized here, so no user-supplied
    /// SVG (which may contain scripts) is ever served back; the result is always WebP.
    /// </summary>
    public static (byte[] Data, int Width, int Height) ProcessImage(ReadOnlySpan<byte> input)
    {
        using var activity = Telemetry.AnubisTelemetry.ActivitySource.StartActivity("avatar.process");
        activity?.SetTag("anubis.image.bytes", input.Length);
        using var bitmap = ImageProcessing.Decode(input, MaxDimension);
        return (ImageProcessing.EncodeWebp(bitmap), bitmap.Width, bitmap.Height);
    }

    public static (int Width, int Height) ComputeDimensions(int width, int height, int maxDimension = MaxDimension)
        => ImageProcessing.ComputeDimensions(width, height, maxDimension);

    public async Task<UserAvatar> SetAvatarAsync(User user, ReadOnlyMemory<byte> imageBytes, CancellationToken cancellationToken = default)
    {
        var (webpBytes, width, height) = ProcessImage(imageBytes.Span);
        var now = DateTimeOffset.UtcNow;

        var avatar = await _db.UserAvatars.FindAsync([user.Id], cancellationToken);
        if (avatar is null)
        {
            avatar = new UserAvatar
            {
                UserId = user.Id,
                Data = webpBytes,
                ContentType = WebpContentType,
                Width = width,
                Height = height,
                UpdatedAt = now,
            };
            _db.UserAvatars.Add(avatar);
        }
        else
        {
            avatar.Data = webpBytes;
            avatar.ContentType = WebpContentType;
            avatar.Width = width;
            avatar.Height = height;
            avatar.UpdatedAt = now;
        }

        user.AvatarUrl = $"/avatars/{user.Id}?v={now.ToUnixTimeMilliseconds()}";
        await _db.SaveChangesAsync(cancellationToken);
        await _userManager.UpdateAsync(user);
        await _activity.RecordAsync(user.Id, Activity.UserEventTypes.AvatarChanged, cancellationToken: cancellationToken);
        return avatar;
    }

    public async Task RemoveAvatarAsync(User user, CancellationToken cancellationToken = default)
    {
        var avatar = await _db.UserAvatars.FindAsync([user.Id], cancellationToken);
        if (avatar is not null)
        {
            _db.UserAvatars.Remove(avatar);
        }

        user.AvatarUrl = null;
        await _db.SaveChangesAsync(cancellationToken);
        await _userManager.UpdateAsync(user);
        await _activity.RecordAsync(user.Id, Activity.UserEventTypes.AvatarChanged, cancellationToken: cancellationToken);
    }

    public Task<UserAvatar?> GetAvatarAsync(string userId, CancellationToken cancellationToken = default)
        => _db.UserAvatars.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
}

