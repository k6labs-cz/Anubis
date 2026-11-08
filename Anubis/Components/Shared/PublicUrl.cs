namespace k6labs.Anubis.Components.Shared;

/// <summary>Builds absolute links for emails.</summary>
public static class PublicUrl
{
    /// <summary>
    /// Uses the configured issuer when set. Otherwise falls back to the request's base URL,
    /// which comes from the Host header, so set <c>Anubis:Issuer</c> in production.
    /// </summary>
    public static string Combine(string? issuer, string requestBaseUri, string pathAndQuery)
    {
        var baseUrl = string.IsNullOrWhiteSpace(issuer) ? requestBaseUri : issuer;
        return baseUrl.TrimEnd('/') + "/" + pathAndQuery.TrimStart('/');
    }
}
