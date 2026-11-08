using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

using QRCoder;

namespace k6labs.Anubis.Identity;

/// <summary>Helpers for setting up a TOTP authenticator app.</summary>
public static class AuthenticatorSetup
{
    public const string Issuer = "Anubis";

    public const int RecoveryCodeCount = 10;

    /// <summary>Formats the base32 key in groups of four for manual entry.</summary>
    public static string FormatKey(string key)
    {
        var result = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            if (i > 0)
            {
                result.Append(' ');
            }

            result.Append(key.AsSpan(i, Math.Min(4, key.Length - i)));
        }

        return result.ToString().ToLowerInvariant();
    }

    /// <summary>The otpauth:// URI understood by authenticator apps (Key Uri Format).</summary>
    public static string BuildUri(string accountName, string key)
        => string.Format(
            CultureInfo.InvariantCulture,
            "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6",
            UrlEncoder.Default.Encode(Issuer),
            UrlEncoder.Default.Encode(accountName),
            key);

    /// <summary>Renders the URI as a PNG QR code data: URI (allowed by the CSP img-src).</summary>
    public static string BuildQrCodeDataUri(string uri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 6);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    /// <summary>Removes spaces and dashes users often type into codes.</summary>
    public static string NormalizeCode(string code)
        => code.Replace(" ", string.Empty).Replace("-", string.Empty);
}
