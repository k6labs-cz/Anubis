using k6labs.Anubis.Options;
using k6labs.Hathor.Theme;

namespace k6labs.Anubis.Services;

/// <summary>
/// Turns the <c>Anubis:Appearance</c> configuration into Hathor theme parameters (rendered by
/// App.razor as CSS custom properties on the Hathor root). Unsafe values are left out, so the
/// Hathor defaults apply.
/// </summary>
public static class AppearanceTheme
{
    /// <summary>A value that can't break out of a CSS declaration (no ; { } &lt; &gt; \ or line breaks).</summary>
    public static bool IsSafeCssValue(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.IndexOfAny([';', '{', '}', '<', '>', '\\', '\n', '\r']) < 0;

    public static ThemePalette? Palette(ThemeColors colors)
    {
        var palette = new ThemePalette
        {
            Primary = Safe(colors.Primary),
            Secondary = Safe(colors.Secondary),
            Background = Safe(colors.Background),
            Text = Safe(colors.Foreground),
        };

        return palette.Primary is null && palette.Secondary is null && palette.Background is null && palette.Text is null
            ? null
            : palette;
    }

    public static string? FontFamily(AppearanceOptions options)
        => IsSafeCssValue(options.FontFamily) ? options.FontFamily!.Trim() : null;

    private static string? Safe(string? value) => IsSafeCssValue(value) ? value!.Trim() : null;
}
