using System.Globalization;

namespace k6labs.Anubis.Localization;

/// <summary>Languages the UI and emails are available in.</summary>
public static class Cultures
{
    public const string Default = "en";

    /// <summary>
    /// The 24 official EU languages (two-letter codes) in the EU's protocol order, i.e. sorted
    /// by each language's own name. Each needs a Resources/Translations.&lt;code&gt;.resx (except the default).
    /// </summary>
    public static readonly IReadOnlyList<string> Supported =
        ["bg", "es", "cs", "da", "de", "et", "el", "en", "fr", "ga", "hr", "it", "lv", "lt", "hu", "mt", "nl", "pl", "pt", "ro", "sk", "sl", "fi", "sv"];

    private static readonly Dictionary<string, string> NativeNames = new()
    {
        ["bg"] = "Български",
        ["es"] = "Español",
        ["cs"] = "Čeština",
        ["da"] = "Dansk",
        ["de"] = "Deutsch",
        ["et"] = "Eesti",
        ["el"] = "Ελληνικά",
        ["en"] = "English",
        ["fr"] = "Français",
        ["ga"] = "Gaeilge",
        ["hr"] = "Hrvatski",
        ["it"] = "Italiano",
        ["lv"] = "Latviešu",
        ["lt"] = "Lietuvių",
        ["hu"] = "Magyar",
        ["mt"] = "Malti",
        ["nl"] = "Nederlands",
        ["pl"] = "Polski",
        ["pt"] = "Português",
        ["ro"] = "Română",
        ["sk"] = "Slovenčina",
        ["sl"] = "Slovenščina",
        ["fi"] = "Suomi",
        ["sv"] = "Svenska",
    };

    /// <summary>The language's own name, shown in the language switcher.</summary>
    public static string NativeName(string code) => NativeNames.GetValueOrDefault(code, NativeNames[Default]);

    /// <summary>Maps any tag (e.g. "cs-CZ", "EN") to a supported code, or null.</summary>
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var code = tag.Trim().Split('-', '_')[0].ToLowerInvariant();
        return Supported.Contains(code) ? code : null;
    }

    /// <summary>The culture for a user's saved language, falling back to the current UI culture.</summary>
    public static CultureInfo ForUser(string? language)
        => Normalize(language) is { } code ? CultureInfo.GetCultureInfo(code) : CultureInfo.CurrentUICulture;

    /// <summary>Runs <paramref name="action"/> with the given UI culture (resources read CurrentUICulture).</summary>
    public static T With<T>(CultureInfo culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentUICulture;
        var previousFormat = CultureInfo.CurrentCulture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
            CultureInfo.CurrentCulture = previousFormat;
        }
    }
}
