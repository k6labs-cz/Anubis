namespace k6labs.Anubis.Services;

/// <summary>Values of <see cref="Data.Entities.User.ColorScheme"/> (null = follow the system).</summary>
public static class ColorSchemes
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string System = "system";

    /// <summary>"light" / "dark" as stored; anything else (incl. "system") becomes null.</summary>
    public static string? Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Light => Light,
        Dark => Dark,
        _ => null,
    };
}
