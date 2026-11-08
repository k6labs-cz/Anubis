namespace k6labs.Anubis.Options;

/// <summary>
/// Appearance options bound to the <c>Anubis:Appearance</c> configuration section.
/// Everything is plain configuration, so it can be set via appsettings, environment
/// variables (e.g. <c>Anubis__Appearance__Light__Primary</c>) or a mounted config in k8s.
/// </summary>
public sealed class AppearanceOptions
{
    public const string SectionName = "Anubis:Appearance";

    /// <summary>The Anubis pyramid (SVG) as a data URL: the default logo, kept in configuration, not in wwwroot.</summary>
    public const string DefaultLogo = "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA1MDAgNTAwIiB3aWR0aD0iNTAwIiBoZWlnaHQ9IjUwMCI+CiAgPCEtLSBQb3phZMOtICh0cmFuc3BhcmVudG7DrSkgLS0+CgogIDxnIGlkPSJweXJhbWlkIj4KICAgIDwhLS0gSG9ybsOtIGxldsOhIHN0xJtuYSAoQ3lhbiAvIFR5cmt5cykgLS0+CiAgICA8cG9seWdvbiBwb2ludHM9IjI1MCw3NSA1MCwzMjUgMjQ2LDI3NSAyNDYsNzUiIGZpbGw9IiMwMGM4ZTYiIC8+CgogICAgPCEtLSBIb3Juw60gcHJhdsOhIHN0xJtuYSAoTWFnZW50YSAvIFLFr8W+b3bDoSkgLS0+CiAgICA8cG9seWdvbiBwb2ludHM9IjI1MCw3NSAyNTQsNzUgMjU0LDI3NSA0NTAsMzI1IiBmaWxsPSIjZTQxYjdiIiAvPgoKICAgIDwhLS0gU3BvZG7DrSBsZXbDoSBzdMSbbmEgKFRtYXbDvSB0ZWFsIC8gUGV0cm9sZWpvdsOhKSAtLT4KICAgIDxwb2x5Z29uIHBvaW50cz0iNTAsMzI1IDI0Niw0MjUgMjQ2LDI3NSIgZmlsbD0iIzAwODM5YyIgLz4KCiAgICA8IS0tIFNwb2Ruw60gcHJhdsOhIHN0xJtuYSAoVsOtbm92w6EgLyBUbWF2w6EgbWFnZW50YSkgLS0+CiAgICA8cG9seWdvbiBwb2ludHM9IjQ1MCwzMjUgMjU0LDI3NSAyNTQsNDI1IiBmaWxsPSIjOGYwYzQzIiAvPgoKICAgIDwhLS0gU3Zpc2zDoSBkxJtsw61jw60gc3DDoXJhIChCw61sw6EgbGlua2EpIC0tPgogICAgPHJlY3QgeD0iMjQ2IiB5PSI3NSIgd2lkdGg9IjgiIGhlaWdodD0iMzUwIiBmaWxsPSIjZmZmZmZmIiBvcGFjaXR5PSIwLjkiIC8+CiAgPC9nPgoKICA8IS0tIEtsw63EjW92w6EgZMOtcmthICjEjGVybsOhIHNpbHVldGEgdnljZW50cm92YW7DoSBuYSBzdMWZZWQpIC0tPgogIDxnIGlkPSJrZXlob2xlIiBmaWxsPSIjMDAwMDAwIj4KICAgIDwhLS0gS3J1aG92w6EgaGxhdmEga2zDrcSNb3bDqSBkw61ya3kgLS0+CiAgICA8Y2lyY2xlIGN4PSIyNTAiIGN5PSIyMTUiIHI9IjQyIiAvPgogICAgCiAgICA8IS0tIEvDs25pY2vDqSB0xJtsbyBrbMOtxI1vdsOpIGTDrXJreSAtLT4KICAgIDxwb2x5Z29uIHBvaW50cz0iMjI4LDIzNSAyNzIsMjM1IDI4NiwzMzAgMjE0LDMzMCIgLz4KICA8L2c+Cjwvc3ZnPg==";

    /// <summary>Default favicon (the same pyramid), as a data URL.</summary>
    public const string DefaultFavicon = "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA1MDAgNTAwIiB3aWR0aD0iNTAwIiBoZWlnaHQ9IjUwMCI+CiAgPCEtLSBQb3phZMOtICh0cmFuc3BhcmVudG7DrSkgLS0+CgogIDxnIGlkPSJweXJhbWlkIj4KICAgIDwhLS0gSG9ybsOtIGxldsOhIHN0xJtuYSAoQ3lhbiAvIFR5cmt5cykgLS0+CiAgICA8cG9seWdvbiBwb2ludHM9IjI1MCw3NSA1MCwzMjUgMjQ2LDI3NSAyNDYsNzUiIGZpbGw9IiMwMGM4ZTYiIC8+CgogICAgPCEtLSBIb3Juw60gcHJhdsOhIHN0xJtuYSAoTWFnZW50YSAvIFLFr8W+b3bDoSkgLS0+CiAgICA8cG9seWdvbiBwb2ludHM9IjI1MCw3NSAyNTQsNzUgMjU0LDI3NSA0NTAsMzI1IiBmaWxsPSIjZTQxYjdiIiAvPgoKICAgIDwhLS0gU3BvZG7DrSBsZXbDoSBzdMSbbmEgKFRtYXbDvSB0ZWFsIC8gUGV0cm9sZWpvdsOhKSAtLT4KICAgIDxwb2x5Z29uIHBvaW50cz0iNTAsMzI1IDI0Niw0MjUgMjQ2LDI3NSIgZmlsbD0iIzAwODM5YyIgLz4KCiAgICA8IS0tIFNwb2Ruw60gcHJhdsOhIHN0xJtuYSAoVsOtbm92w6EgLyBUbWF2w6EgbWFnZW50YSkgLS0+CiAgICA8cG9seWdvbiBwb2ludHM9IjQ1MCwzMjUgMjU0LDI3NSAyNTQsNDI1IiBmaWxsPSIjOGYwYzQzIiAvPgoKICAgIDwhLS0gU3Zpc2zDoSBkxJtsw61jw60gc3DDoXJhIChCw61sw6EgbGlua2EpIC0tPgogICAgPHJlY3QgeD0iMjQ2IiB5PSI3NSIgd2lkdGg9IjgiIGhlaWdodD0iMzUwIiBmaWxsPSIjZmZmZmZmIiBvcGFjaXR5PSIwLjkiIC8+CiAgPC9nPgoKICA8IS0tIEtsw63EjW92w6EgZMOtcmthICjEjGVybsOhIHNpbHVldGEgdnljZW50cm92YW7DoSBuYSBzdMWZZWQpIC0tPgogIDxnIGlkPSJrZXlob2xlIiBmaWxsPSIjMDAwMDAwIj4KICAgIDwhLS0gS3J1aG92w6EgaGxhdmEga2zDrcSNb3bDqSBkw61ya3kgLS0+CiAgICA8Y2lyY2xlIGN4PSIyNTAiIGN5PSIyMTUiIHI9IjQyIiAvPgogICAgCiAgICA8IS0tIEvDs25pY2vDqSB0xJtsbyBrbMOtxI1vdsOpIGTDrXJreSAtLT4KICAgIDxwb2x5Z29uIHBvaW50cz0iMjI4LDIzNSAyNzIsMjM1IDI4NiwzMzAgMjE0LDMzMCIgLz4KICA8L2c+Cjwvc3ZnPg==";

    /// <summary>Logo on the sign-in page: a data URL (default / uploaded), a path under wwwroot or an https:// URL.</summary>
    public string LogoPath { get; set; } = DefaultLogo;

    /// <summary>Favicon: a data URL (default / uploaded), a path under wwwroot or an https:// URL.</summary>
    public string FaviconPath { get; set; } = DefaultFavicon;

    /// <summary>
    /// CSS font-family stack used for the whole UI (the Hathor <c>FontFamily</c>), from fonts
    /// installed on the device. Empty = the Hathor default (system monospace). Example: <c>"Segoe UI", sans-serif</c>.
    /// </summary>
    public string? FontFamily { get; set; }

    /// <summary>Color overrides for the light theme. Empty values keep the Anubis defaults.</summary>
    public ThemeColors Light { get; set; } = new();

    /// <summary>Color overrides for the dark theme.</summary>
    public ThemeColors Dark { get; set; } = new();
}

/// <summary>Theme color overrides for one mode. Any valid CSS color (hex, rgb(), oklch(), ...).</summary>
public sealed class ThemeColors
{
    /// <summary>Primary accent (buttons, links, focus ring). Hathor <c>--primary</c>.</summary>
    public string? Primary { get; set; }

    /// <summary>Secondary accent. Hathor <c>--secondary</c>.</summary>
    public string? Secondary { get; set; }

    /// <summary>Page background. Hathor <c>--bg</c>.</summary>
    public string? Background { get; set; }

    /// <summary>Default text color. Hathor <c>--text</c>.</summary>
    public string? Foreground { get; set; }
}
