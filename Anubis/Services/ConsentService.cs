using System.Globalization;
using System.Security.Cryptography;

using k6labs.Anubis.Options;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace k6labs.Anubis.Services;

/// <summary>
/// Tracks per-user consent for OIDC clients during the authorization code flow.
/// The consent is stored in a short-lived, HttpOnly, data-protected cookie so that
/// re-authorization within the cookie lifetime does not ask the user again.
/// </summary>
public interface IConsentService
{
    public void GrantConsent(HttpContext httpContext, string userId, string clientId, IEnumerable<string> scopes);

    public bool HasConsent(HttpContext httpContext, string userId, string clientId, IEnumerable<string> scopes);
}

public sealed class ConsentService : IConsentService
{
    public const string CookieName = "anubis.consent";

    private const string ProtectorPurpose = "Anubis.Consent.v1";

    private readonly IDataProtector _protector;
    private readonly AnubisOptions _options;

    public ConsentService(IDataProtectionProvider dataProtectionProvider, IOptions<AnubisOptions> options)
    {
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _options = options.Value;
    }

    public void GrantConsent(HttpContext httpContext, string userId, string clientId, IEnumerable<string> scopes)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(_options.ConsentCookieLifetimeMinutes);
        var payload = string.Join('|',
            userId,
            clientId,
            NormalizeScopes(scopes),
            expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        httpContext.Response.Cookies.Append(CookieName, _protector.Protect(payload), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = httpContext.Request.IsHttps,
            Path = "/",
            Expires = expires,
        });
    }

    public bool HasConsent(HttpContext httpContext, string userId, string clientId, IEnumerable<string> scopes)
    {
        if (!httpContext.Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return false;
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return false;
        }

        var parts = payload.Split('|');
        if (parts.Length != 4 || !long.TryParse(parts[3], CultureInfo.InvariantCulture, out var expiresUnix))
        {
            return false;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(expiresUnix) < DateTimeOffset.UtcNow)
        {
            return false;
        }

        return parts[0] == userId
            && parts[1] == clientId
            && parts[2] == NormalizeScopes(scopes);
    }

    private static string NormalizeScopes(IEnumerable<string> scopes)
        => string.Join(' ', scopes
            .Select(scope => scope.Trim())
            .Where(scope => scope.Length > 0)
            .OrderBy(scope => scope, StringComparer.Ordinal));
}
