using System.Globalization;

namespace k6labs.Anubis.Resources;

public static class StringExtensions
{
    /// <summary>Short form used in Razor markup to format a localized string.</summary>
    public static string F(this string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, format, args);

    /// <summary>Formats a localized string: <c>Translations.Users_Deleted.Format(email)</c>.</summary>
    public static string Format(this string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, format, args);
}
