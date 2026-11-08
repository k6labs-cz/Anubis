using System.Text;
using System.Xml;

using SkiaSharp;

using Svg.Skia;

namespace k6labs.Anubis.Services;

/// <summary>
/// Shared image handling for avatars, the logo and the favicon (SkiaSharp + Svg.Skia):
/// format detection, SVG safety checks, SVG rasterization and WebP/PNG encoding.
/// </summary>
public static class ImageProcessing
{
    public const string SvgContentType = "image/svg+xml";
    public const string WebpContentType = "image/webp";
    public const string PngContentType = "image/png";

    /// <summary>Caps against SVGs built to exhaust the CPU while rendering (avatars come from any user).</summary>
    private const int MaxSvgElements = 20_000;
    private const int MaxSvgUseElements = 500;

    /// <summary>Size used for SVGs that declare neither width/height nor a viewBox.</summary>
    private const int DefaultSvgSize = 512;

    /// <summary>True when the bytes look like an SVG document (optionally with BOM, XML declaration, comments).</summary>
    public static bool IsSvg(ReadOnlySpan<byte> data)
    {
        var head = Encoding.UTF8.GetString(data[..Math.Min(data.Length, 2048)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.StartsWith('<') && head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rejects SVGs that could reach outside the document: DTDs/entities (XXE, entity
    /// expansion) and links to anything other than an internal <c>#id</c> or a <c>data:</c>
    /// image (so neither the rasterizer nor a browser ever loads a remote or local file).
    /// Very large documents are refused too.
    /// Scripts are harmless here: SVGs are only rasterized, shown via <c>&lt;img&gt;</c>, or
    /// served with a sandboxing Content-Security-Policy.
    /// </summary>
    /// <exception cref="InvalidDataException">The SVG is malformed or not allowed.</exception>
    public static void EnsureSafeSvg(ReadOnlySpan<byte> data)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            MaxCharactersInDocument = 20_000_000,
        };

        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, settings);
            var sawRoot = false;
            var elements = 0;
            var uses = 0;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (++elements > MaxSvgElements || (reader.LocalName == "use" && ++uses > MaxSvgUseElements))
                {
                    throw new InvalidDataException("The SVG is too complex.");
                }

                if (!sawRoot)
                {
                    if (!string.Equals(reader.LocalName, "svg", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The root element is not <svg>.");
                    }

                    sawRoot = true;
                }

                if (!reader.HasAttributes)
                {
                    continue;
                }

                while (reader.MoveToNextAttribute())
                {
                    if (reader.LocalName == "href" && !IsInternalReference(reader.Value))
                    {
                        throw new InvalidDataException("The SVG links to an external resource.");
                    }

                    if (reader.Value.Contains("url(", StringComparison.OrdinalIgnoreCase)
                        && !reader.Value.Replace(" ", string.Empty).Contains("url(#", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The SVG references an external resource.");
                    }
                }

                reader.MoveToElement();
            }

            if (!sawRoot)
            {
                throw new InvalidDataException("No <svg> element found.");
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("Invalid SVG: " + exception.Message, exception);
        }
    }

    /// <summary>Decodes any supported image (raster formats via Skia, SVG via Svg.Skia) into a bitmap no larger than <paramref name="maxDimension"/>.</summary>
    /// <exception cref="InvalidDataException">The image is empty, malformed or unsupported.</exception>
    public static SKBitmap Decode(ReadOnlySpan<byte> data, int maxDimension)
    {
        if (data.IsEmpty)
        {
            throw new InvalidDataException("Image data is empty.");
        }

        return IsSvg(data) ? RasterizeSvg(data, maxDimension) : DecodeRaster(data, maxDimension);
    }

    /// <summary>Renders an SVG so that its longer side is <paramref name="maxDimension"/> pixels (transparent background).</summary>
    public static SKBitmap RasterizeSvg(ReadOnlySpan<byte> data, int maxDimension, bool square = false)
    {
        EnsureSafeSvg(data);

        using var svg = new SKSvg();
        using (var stream = new MemoryStream(data.ToArray(), writable: false))
        {
            svg.Load(stream);
        }

        var picture = svg.Picture ?? throw new InvalidDataException("The SVG could not be rendered.");
        var bounds = picture.CullRect;
        var sourceWidth = bounds.Width > 0 ? bounds.Width : DefaultSvgSize;
        var sourceHeight = bounds.Height > 0 ? bounds.Height : DefaultSvgSize;

        // SVGs scale freely: always render at the requested size, not the declared one.
        var scale = Math.Min(maxDimension / sourceWidth, maxDimension / sourceHeight);
        var width = square ? maxDimension : Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = square ? maxDimension : Math.Max(1, (int)Math.Round(sourceHeight * scale));

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate((width - (sourceWidth * scale)) / 2f, (height - (sourceHeight * scale)) / 2f);
        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Flush();
        return bitmap;
    }

    /// <summary>Scales a bitmap down (never up) so it fits into <paramref name="maxDimension"/>; returns the same instance when it already fits.</summary>
    public static SKBitmap FitWithin(SKBitmap source, int maxDimension)
    {
        var (width, height) = ComputeDimensions(source.Width, source.Height, maxDimension);
        if (width == source.Width && height == source.Height)
        {
            return source;
        }

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        return source.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidDataException("Failed to resize image.");
    }

    /// <summary>Draws the bitmap centered on a transparent square canvas (for icons).</summary>
    public static SKBitmap ToSquare(SKBitmap source, int size)
    {
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var scale = Math.Min((float)size / source.Width, (float)size / source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;
        var target = SKRect.Create((size - width) / 2f, (size - height) / 2f, width, height);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.Flush();
        return bitmap;
    }

    public static (int Width, int Height) ComputeDimensions(int width, int height, int maxDimension)
    {
        if (width <= maxDimension && height <= maxDimension)
        {
            return (width, height);
        }

        var scale = Math.Min((double)maxDimension / width, (double)maxDimension / height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>Encodes as WebP: lossless for logos/icons (sharp edges), lossy quality 90 for photos.</summary>
    public static byte[] EncodeWebp(SKBitmap bitmap, bool lossless = false)
    {
        using var pixmap = bitmap.PeekPixels() ?? throw new InvalidDataException("Failed to read image pixels.");
        var options = lossless
            ? new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 75)
            : new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 90);
        using var encoded = pixmap.Encode(options) ?? throw new InvalidDataException("Failed to encode WebP image.");
        return encoded.ToArray();
    }

    public static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("Failed to encode PNG image.");
        return encoded.ToArray();
    }

    private static SKBitmap DecodeRaster(ReadOnlySpan<byte> data, int maxDimension)
    {
        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData) ?? throw new InvalidDataException("Unsupported or corrupted image format.");

        if (codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            throw new InvalidDataException("Invalid image dimensions.");
        }

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var source = SKBitmap.Decode(codec, info) ?? throw new InvalidDataException("Failed to decode image.");

        var fitted = FitWithin(source, maxDimension);
        if (!ReferenceEquals(fitted, source))
        {
            source.Dispose();
        }

        return fitted;
    }

    private static bool IsInternalReference(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0
            || trimmed.StartsWith('#')
            || trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
    }
}
