namespace k6labs.Anubis.Options;

/// <summary>
/// Appearance options bound to the <c>Anubis:Appearance</c> configuration section.
/// Everything is plain configuration, so it can be set via appsettings, environment
/// variables (e.g. <c>Anubis__Appearance__Light__Primary</c>) or a mounted config in k8s.
/// </summary>
public sealed class AppearanceOptions
{
    public const string SectionName = "Anubis:Appearance";

    /// <summary>The Anubis logo (pyramid with an ankh, anubis.svg without editor metadata) as a data URL: the default logo, kept in configuration, not in wwwroot.</summary>
    public const string DefaultLogo = "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA1MDAgNTAwIiB3aWR0aD0iNTAwIiBoZWlnaHQ9IjUwMCI+CiAgPHBvbHlnb24gcG9pbnRzPSIyNDYsMjc1IDI0Niw3NSAyNTAsNzUgNTAsMzI1IiBmaWxsPSIjMDBjOGU2Ii8+CiAgPHBvbHlnb24gcG9pbnRzPSIyNTQsMjc1IDQ1MCwzMjUgMjUwLDc1IDI1NCw3NSIgZmlsbD0iI2U0MWI3YiIvPgogIDxwb2x5Z29uIHBvaW50cz0iMjQ2LDI3NSA1MCwzMjUgMjQ2LDQyNSIgZmlsbD0iIzAwODM5YyIvPgogIDxwb2x5Z29uIHBvaW50cz0iMjU0LDQyNSA0NTAsMzI1IDI1NCwyNzUiIGZpbGw9IiM4ZjBjNDMiLz4KICA8cmVjdCB4PSIyNDYiIHk9Ijc1IiB3aWR0aD0iOCIgaGVpZ2h0PSIzNTAiIGZpbGw9IiNmZmZmZmYiIG9wYWNpdHk9IjAuOSIvPgogIDxnIHRyYW5zZm9ybT0ibWF0cml4KDAuNDI1LDAsMCwwLjQzLDE0MS4yLDEzOS45MikiIGZpbGw9IiMwMDAwMDAiIHN0cm9rZT0iIzAwMDAwMCIgc3Ryb2tlLXdpZHRoPSIyOC4wNzA2NTU0MyIgc3Ryb2tlLWxpbmVjYXA9InJvdW5kIiBzdHJva2UtbGluZWpvaW49InJvdW5kIj4KICAgIDxwYXRoIGQ9Ik0gMjMyLDI1NSBDIDE5NywyMTcgMTYzLDE1MSAxNjMsMTA1IDE2Myw0MiAxOTksOSAyNTYsOSBjIDU3LDAgOTMsMzMgOTMsOTYgMCw0NiAtMzQsMTEyIC02OSwxNTAgbCAxNTUsLTcgdiA4NCBMIDI4MCwzMTUgMzE2LDUwMyBIIDE5NiBMIDIzMiwzMTUgNzcsMzMyIHYgLTg0IHoiLz4KICAgIDxwYXRoIGQ9Im0gMjU2LDIwNyBjIC0yMSwtMjYgLTQyLC02OCAtNDIsLTEwMiAwLC0yOCAxNywtNDUgNDIsLTQ1IDI1LDAgNDIsMTcgNDIsNDUgMCwzNCAtMjEsNzYgLTQyLDEwMiB6Ii8+CiAgPC9nPgo8L3N2Zz4K";

    /// <summary>Default favicon (the same logo), as a data URL.</summary>
    public const string DefaultFavicon = "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA1MDAgNTAwIiB3aWR0aD0iNTAwIiBoZWlnaHQ9IjUwMCI+CiAgPHBvbHlnb24gcG9pbnRzPSIyNDYsMjc1IDI0Niw3NSAyNTAsNzUgNTAsMzI1IiBmaWxsPSIjMDBjOGU2Ii8+CiAgPHBvbHlnb24gcG9pbnRzPSIyNTQsMjc1IDQ1MCwzMjUgMjUwLDc1IDI1NCw3NSIgZmlsbD0iI2U0MWI3YiIvPgogIDxwb2x5Z29uIHBvaW50cz0iMjQ2LDI3NSA1MCwzMjUgMjQ2LDQyNSIgZmlsbD0iIzAwODM5YyIvPgogIDxwb2x5Z29uIHBvaW50cz0iMjU0LDQyNSA0NTAsMzI1IDI1NCwyNzUiIGZpbGw9IiM4ZjBjNDMiLz4KICA8cmVjdCB4PSIyNDYiIHk9Ijc1IiB3aWR0aD0iOCIgaGVpZ2h0PSIzNTAiIGZpbGw9IiNmZmZmZmYiIG9wYWNpdHk9IjAuOSIvPgogIDxnIHRyYW5zZm9ybT0ibWF0cml4KDAuNDI1LDAsMCwwLjQzLDE0MS4yLDEzOS45MikiIGZpbGw9IiMwMDAwMDAiIHN0cm9rZT0iIzAwMDAwMCIgc3Ryb2tlLXdpZHRoPSIyOC4wNzA2NTU0MyIgc3Ryb2tlLWxpbmVjYXA9InJvdW5kIiBzdHJva2UtbGluZWpvaW49InJvdW5kIj4KICAgIDxwYXRoIGQ9Ik0gMjMyLDI1NSBDIDE5NywyMTcgMTYzLDE1MSAxNjMsMTA1IDE2Myw0MiAxOTksOSAyNTYsOSBjIDU3LDAgOTMsMzMgOTMsOTYgMCw0NiAtMzQsMTEyIC02OSwxNTAgbCAxNTUsLTcgdiA4NCBMIDI4MCwzMTUgMzE2LDUwMyBIIDE5NiBMIDIzMiwzMTUgNzcsMzMyIHYgLTg0IHoiLz4KICAgIDxwYXRoIGQ9Im0gMjU2LDIwNyBjIC0yMSwtMjYgLTQyLC02OCAtNDIsLTEwMiAwLC0yOCAxNywtNDUgNDIsLTQ1IDI1LDAgNDIsMTcgNDIsNDUgMCwzNCAtMjEsNzYgLTQyLDEwMiB6Ii8+CiAgPC9nPgo8L3N2Zz4K";

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
