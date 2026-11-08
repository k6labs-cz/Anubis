using k6labs.Anubis.Data.Entities;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.WebUtilities;

namespace k6labs.Anubis.Localization;

/// <summary>Uses the language saved on the signed-in user's account (works across devices).</summary>
public sealed class UserLanguageCultureProvider : RequestCultureProvider
{
    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var userManager = httpContext.RequestServices.GetRequiredService<UserManager<User>>();
        var user = await userManager.GetUserAsync(httpContext.User);
        return Cultures.Normalize(user?.Language) is { } code ? new ProviderCultureResult(code) : null;
    }
}

/// <summary>
/// OpenID Connect <c>ui_locales</c> (Core 1.0, section 3.1.2.1): a client can ask for the
/// login pages in a language. Read from the authorize request, or from the returnUrl
/// that carries it to /login and /consent.
/// </summary>
public sealed class UiLocalesCultureProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var query = httpContext.Request.Query;
        var uiLocales = query["ui_locales"].ToString();

        if (string.IsNullOrEmpty(uiLocales) && query["returnUrl"].ToString() is { Length: > 0 } returnUrl
            && Uri.TryCreate(new Uri("http://local"), returnUrl, out var uri))
        {
            uiLocales = QueryHelpers.ParseQuery(uri.Query).TryGetValue("ui_locales", out var value) ? value.ToString() : string.Empty;
        }

        // Space-separated list in order of preference; take the first supported one.
        var code = uiLocales.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Cultures.Normalize).FirstOrDefault(c => c is not null);
        return Task.FromResult(code is null ? null : new ProviderCultureResult(code));
    }
}
