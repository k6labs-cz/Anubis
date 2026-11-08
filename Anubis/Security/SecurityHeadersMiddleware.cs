using k6labs.Anubis.Options;

using Microsoft.Extensions.Options;

namespace k6labs.Anubis.Security;

/// <summary>Adds baseline security headers to every response.</summary>
public static class SecurityHeadersMiddleware
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            // "same-origin", not "no-referrer": with no-referrer browsers send "Origin: null" on
            // form posts, and passkey verification (which compares the Origin header with the
            // WebAuthn origin) always fails. Other sites still get no referrer.
            headers["Referrer-Policy"] = "same-origin";

            // Avatars come from Gravatar or a custom AvatarUrl (any https host).
            var imgSrc = "img-src 'self' data: https:";

            // Hathor sets its theme tokens as inline style attributes; there are no external stylesheets or fonts.
            var styleSrc = "style-src 'self' 'unsafe-inline'";
            var fontSrc = "font-src 'self'";

            headers.ContentSecurityPolicy =
                "default-src 'self'; " +
                "script-src 'self'; " +
                styleSrc + "; " +
                imgSrc + "; " +
                fontSrc + "; " +
                "connect-src 'self' ws: wss:; " +
                "frame-ancestors 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self'";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

            await next();
        });
}
