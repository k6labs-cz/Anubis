namespace k6labs.Anubis.Services.Activity;

/// <summary>Short, human readable device description from a User-Agent ("Firefox · Windows").</summary>
public static class UserAgentInfo
{
    public static string? Describe(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        var browser = Browser(userAgent);
        var system = OperatingSystem(userAgent);
        return (browser, system) switch
        {
            (null, null) => null,
            (null, _) => system,
            (_, null) => browser,
            _ => $"{browser} · {system}",
        };
    }

    private static string? Browser(string ua)
    {
        if (ua.Contains("Edg/", StringComparison.Ordinal)) return "Edge";
        if (ua.Contains("OPR/", StringComparison.Ordinal) || ua.Contains("Opera", StringComparison.Ordinal)) return "Opera";
        if (ua.Contains("SamsungBrowser/", StringComparison.Ordinal)) return "Samsung Internet";
        if (ua.Contains("Firefox/", StringComparison.Ordinal) || ua.Contains("FxiOS/", StringComparison.Ordinal)) return "Firefox";
        if (ua.Contains("Chrome/", StringComparison.Ordinal) || ua.Contains("CriOS/", StringComparison.Ordinal)) return "Chrome";
        if (ua.Contains("Safari/", StringComparison.Ordinal) && ua.Contains("Version/", StringComparison.Ordinal)) return "Safari";
        return null;
    }

    private static string? OperatingSystem(string ua)
    {
        if (ua.Contains("Android", StringComparison.Ordinal)) return "Android";
        if (ua.Contains("iPhone", StringComparison.Ordinal)) return "iOS";
        if (ua.Contains("iPad", StringComparison.Ordinal)) return "iPadOS";
        if (ua.Contains("Windows", StringComparison.Ordinal)) return "Windows";
        if (ua.Contains("CrOS", StringComparison.Ordinal)) return "ChromeOS";
        if (ua.Contains("Mac OS X", StringComparison.Ordinal) || ua.Contains("Macintosh", StringComparison.Ordinal)) return "macOS";
        if (ua.Contains("Linux", StringComparison.Ordinal)) return "Linux";
        return null;
    }
}
