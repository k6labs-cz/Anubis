namespace k6labs.Anubis.Components.Shared;

/// <summary>Helpers for validating that a URL is a local application URL (open-redirect protection).</summary>
public static class LocalUrl
{
    public static bool IsLocal(string? url)
        => !string.IsNullOrEmpty(url)
            && url[0] == '/'
            && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

    /// <summary>The query string of a local URL like <c>/path?a=1</c> (a relative Uri can't give it).</summary>
    public static string QueryOf(string url)
        => url.IndexOf('?') is var index and >= 0 ? url[index..] : string.Empty;
}
