using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace k6labs.Anubis.Services;

/// <summary>
/// JSON file with settings edited in the admin UI (Appearance, Email). It is added as the
/// last configuration source, so its values win over appsettings.json and environment
/// variables. After every save the configuration is reloaded, so <c>IOptionsMonitor</c>
/// consumers see the new values without a restart.
/// </summary>
public sealed class SettingsFile
{
    /// <summary>Configuration key with the file path (relative paths are resolved against the data directory).</summary>
    public const string PathKey = "Anubis:SettingsFile";

    public const string DefaultFileName = "anubis.settings.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IConfigurationRoot? _configuration;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SettingsFile(string path, IConfigurationRoot? configuration)
    {
        Path = path;
        _configuration = configuration;
    }

    /// <summary>Absolute path of the settings file.</summary>
    public string Path { get; }

    public static string ResolvePath(IConfiguration configuration, string dataDirectory)
    {
        var path = configuration[PathKey];
        return System.IO.Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? DefaultFileName : path, dataDirectory);
    }

    /// <summary>Serializes an options object into a JSON section (enums as strings, computed properties skipped).</summary>
    public static JsonObject ToNode<T>(T value)
        => JsonSerializer.SerializeToNode(value, JsonOptions)?.AsObject() ?? new JsonObject();

    /// <summary>Deep copy of an options object, so a form can edit it without touching the live instance.</summary>
    public static T Clone<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;

    /// <summary>Reads one section (e.g. <c>Email</c> or <c>Anubis:Appearance</c>) from the file, or null.</summary>
    public async Task<JsonObject?> ReadSectionAsync(string sectionPath, CancellationToken cancellationToken = default)
    {
        var root = await ReadRootAsync(cancellationToken);
        return Navigate(root, sectionPath, create: false);
    }

    /// <summary>Replaces one section in the file and reloads the configuration.</summary>
    public async Task SaveSectionAsync(string sectionPath, JsonObject section, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var root = await ReadRootAsync(cancellationToken);
            var keys = sectionPath.Split(':');
            var parent = Navigate(root, string.Join(':', keys[..^1]), create: true)!;
            foreach (var key in parent.Select(p => p.Key).Where(k => string.Equals(k, keys[^1], StringComparison.OrdinalIgnoreCase)).ToList())
            {
                parent.Remove(key);
            }

            if (section.Count > 0)
            {
                parent[keys[^1]] = section;
            }
            else
            {
                // An empty section means "back to the defaults": drop it (and parents left empty),
                // so the file never holds empty objects that would shadow lower configuration sources.
                PruneEmpty(root, keys);
            }

            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write to a temp file and swap, so a crash never leaves a half-written file.
            var temp = Path + ".tmp";
            await File.WriteAllTextAsync(temp, root.ToJsonString(JsonOptions), cancellationToken);
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }

        _configuration?.Reload();
    }

    /// <summary>
    /// Sets the given keys of a section (other keys and sub-sections stay), e.g. a few values of
    /// <c>Anubis</c> without touching <c>Anubis:Appearance</c>, then reloads the configuration.
    /// </summary>
    public async Task UpdateSectionAsync(string sectionPath, JsonObject values, CancellationToken cancellationToken = default)
    {
        var current = await ReadSectionAsync(sectionPath, cancellationToken) ?? new JsonObject();
        var merged = (JsonObject)current.DeepClone();
        foreach (var (key, value) in values)
        {
            foreach (var existing in merged.Select(p => p.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                merged.Remove(existing);
            }

            merged[key] = value?.DeepClone();
        }

        await SaveSectionAsync(sectionPath, merged, cancellationToken);
    }

    private async Task<JsonObject> ReadRootAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return new JsonObject();
        }

        var text = await File.ReadAllTextAsync(Path, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? new JsonObject();
    }

    private static void PruneEmpty(JsonObject root, string[] keys)
    {
        for (var depth = keys.Length; depth > 0; depth--)
        {
            var parent = Navigate(root, string.Join(':', keys[..(depth - 1)]), create: false);
            var key = parent?.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, keys[depth - 1], StringComparison.OrdinalIgnoreCase));
            if (parent is null || key is null || parent[key] is JsonObject { Count: > 0 })
            {
                return;
            }

            parent.Remove(key);
        }
    }

    private static JsonObject? Navigate(JsonObject root, string path, bool create)
    {
        var current = root;
        foreach (var key in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            // Configuration keys are case-insensitive; match an existing property regardless of case.
            var existing = current.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing.Value is JsonObject child)
            {
                current = child;
                continue;
            }

            if (!create)
            {
                return null;
            }

            if (existing.Key is not null)
            {
                current.Remove(existing.Key);
            }

            child = new JsonObject();
            current[key] = child;
            current = child;
        }

        return current;
    }
}
