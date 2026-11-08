using System.Security.Cryptography;
using System.Text;

using k6labs.Anubis.Options;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace k6labs.Anubis.Services;

/// <summary>
/// Logo and favicon from <see cref="AppearanceOptions"/>. Each value is a path under wwwroot,
/// an https:// URL, or a <c>data:image/...;base64,</c> URL (what the admin upload stores in the
/// settings file). Data URLs are not inlined into every page: they are served from
/// <c>/appearance/logo</c> and <c>/appearance/favicon</c> with a content hash in the query string,
/// so browsers can cache them. PNG favicons for Safari / iOS are generated from the favicon.
/// </summary>
public sealed class AppearanceAssets
{
    public const int MaxUploadBytes = 10 * 1024 * 1024;
    public const int MaxStoredBytes = 512 * 1024;
    public const int LogoMaxDimension = 1024;
    public const int FaviconMaxDimension = 512;

    /// <summary>Generated PNG favicon sizes: 32 (browser tab), 180 (apple-touch-icon), 192 (Android).</summary>
    public static readonly int[] PngFaviconSizes = [32, 180, 192];

    private static readonly string[] AllowedContentTypes =
        [ImageProcessing.SvgContentType, ImageProcessing.WebpContentType, ImageProcessing.PngContentType, "image/jpeg", "image/gif"];

    private readonly IOptionsMonitor<AppearanceOptions> _options;
    private readonly IWebHostEnvironment _environment;
    private readonly IMemoryCache _cache;

    public AppearanceAssets(IOptionsMonitor<AppearanceOptions> options, IWebHostEnvironment environment, IMemoryCache cache)
    {
        _options = options;
        _environment = environment;
        _cache = cache;
    }

    public AppearanceOptions Current => _options.CurrentValue;

    /// <summary>URL to put into <c>&lt;img src&gt;</c> for the logo.</summary>
    public string LogoUrl => Url(Current.LogoPath, "/appearance/logo");

    /// <summary>URL of the favicon in its original format (SVG, WebP, ...).</summary>
    public string FaviconUrl => Url(Current.FaviconPath, "/appearance/favicon");

    /// <summary>MIME type of the favicon, when known (e.g. <c>image/svg+xml</c>).</summary>
    public string? FaviconContentType
    {
        get
        {
            var value = Current.FaviconPath;
            if (TryParseDataUrl(value, out var contentType, out _))
            {
                return contentType;
            }

            var path = value.Split('?', '#')[0];
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".svg" => ImageProcessing.SvgContentType,
                ".webp" => ImageProcessing.WebpContentType,
                ".png" => ImageProcessing.PngContentType,
                ".ico" => "image/x-icon",
                _ => null,
            };
        }
    }

    /// <summary>URL of a generated PNG favicon, or null when the favicon is a remote URL (it cannot be converted).</summary>
    public string? PngFaviconUrl(int size)
        => IsDataUrl(Current.FaviconPath) || LocalFavicon() is not null ? $"/appearance/favicon-{size}.png?v={FaviconVersion}" : null;

    /// <summary>Decoded logo when it is a data URL (served by <c>/appearance/logo</c>).</summary>
    public (string ContentType, byte[] Data)? LogoData()
        => TryParseDataUrl(Current.LogoPath, out var type, out var data) ? (type, data) : null;

    public (string ContentType, byte[] Data)? FaviconData()
        => TryParseDataUrl(Current.FaviconPath, out var type, out var data) ? (type, data) : null;

    public string LogoVersion => Version(Current.LogoPath);

    /// <summary>For a wwwroot file the modification time is included, so replacing the file busts caches.</summary>
    public string FaviconVersion
        => LocalFavicon() is { } file ? Version($"{Current.FaviconPath}|{file.LastModified.ToUnixTimeSeconds()}") : Version(Current.FaviconPath);

    /// <summary>Square PNG favicon of the given size, generated from the favicon and cached in memory.</summary>
    public byte[]? PngFavicon(int size)
    {
        if (!PngFaviconSizes.Contains(size))
        {
            return null;
        }

        var key = $"favicon-png:{FaviconVersion}:{size}";
        if (_cache.TryGetValue(key, out byte[]? cached))
        {
            return cached;
        }

        var source = ReadFaviconSource();
        if (source is null)
        {
            return null;
        }

        try
        {
            using var decoded = ImageProcessing.Decode(source, size);
            using var square = ImageProcessing.ToSquare(decoded, size);
            var png = ImageProcessing.EncodePng(square);
            _cache.Set(key, png, new MemoryCacheEntryOptions { Size = png.Length, SlidingExpiration = TimeSpan.FromHours(12) });
            return png;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns an uploaded logo/favicon into the value stored in the settings file: SVGs are
    /// checked and kept as they are; raster images are scaled down and re-encoded as lossless
    /// WebP (which also strips metadata).
    /// </summary>
    /// <exception cref="InvalidDataException">Unsupported, broken or (after processing) too large.</exception>
    public static string ProcessUpload(ReadOnlySpan<byte> input, int maxDimension)
    {
        if (input.IsEmpty)
        {
            throw new InvalidDataException("The file is empty.");
        }

        string contentType;
        byte[] data;
        if (ImageProcessing.IsSvg(input))
        {
            // Rendering it once proves Svg.Skia can draw it (needed for the PNG favicons).
            ImageProcessing.RasterizeSvg(input, 16).Dispose();
            contentType = ImageProcessing.SvgContentType;
            data = input.ToArray();
        }
        else
        {
            using var bitmap = ImageProcessing.Decode(input, maxDimension);
            contentType = ImageProcessing.WebpContentType;
            data = ImageProcessing.EncodeWebp(bitmap, lossless: true);
            if (data.Length > MaxStoredBytes)
            {
                data = ImageProcessing.EncodeWebp(bitmap);
            }
        }

        if (data.Length > MaxStoredBytes)
        {
            throw new InvalidDataException("The image is too large.");
        }

        return ToDataUrl(contentType, data);
    }

    public static string ToDataUrl(string contentType, byte[] data)
        => $"data:{contentType};base64,{Convert.ToBase64String(data)}";

    public static bool IsDataUrl(string? value)
        => value is not null && value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses <c>data:image/...;base64,...</c> with one of the allowed image types.</summary>
    public static bool TryParseDataUrl(string? value, out string contentType, out byte[] data)
    {
        contentType = string.Empty;
        data = [];
        if (!IsDataUrl(value))
        {
            return false;
        }

        var comma = value!.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        var header = value[5..comma].Split(';', StringSplitOptions.TrimEntries);
        if (header.Length < 2 || !header.Contains("base64", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var type = header[0].ToLowerInvariant();
        if (!AllowedContentTypes.Contains(type))
        {
            return false;
        }

        try
        {
            data = Convert.FromBase64String(value[(comma + 1)..].Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        contentType = type;
        return data.Length > 0;
    }

    /// <summary>Short content hash used as cache buster.</summary>
    public static string Version(string? value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)))[..12].ToLowerInvariant();

    private static string Url(string value, string endpoint)
        => IsDataUrl(value) ? $"{endpoint}?v={Version(value)}" : value;

    /// <summary>Favicon bytes from a data URL or a file under wwwroot; null for remote URLs.</summary>
    private byte[]? ReadFaviconSource()
    {
        var value = Current.FaviconPath;
        if (TryParseDataUrl(value, out _, out var data))
        {
            return data;
        }

        var file = LocalFavicon();
        if (file is null)
        {
            return null;
        }

        using var stream = file.CreateReadStream();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private Microsoft.Extensions.FileProviders.IFileInfo? LocalFavicon()
    {
        var value = Current.FaviconPath;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        var file = _environment.WebRootFileProvider.GetFileInfo(value.Split('?', '#')[0]);
        return file.Exists && !file.IsDirectory && file.Length <= MaxUploadBytes ? file : null;
    }
}
