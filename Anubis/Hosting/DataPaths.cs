using Microsoft.Data.Sqlite;

namespace k6labs.Anubis.Hosting;

/// <summary>
/// Where Anubis writes its state. Everything lives in one data directory
/// (<c>Anubis:DataDirectory</c>, in a container e.g. <c>/data</c> on a persistent volume):
/// <list type="bullet">
/// <item><c>anubis.db</c> (+ <c>-wal</c>/<c>-shm</c>): the SQLite database,</item>
/// <item><c>anubis.settings.json</c>: settings saved from the admin UI,</item>
/// <item><c>keys/</c>: ASP.NET Core Data Protection keys (cookies, antiforgery, email links)
/// and the OpenIddict signing / encryption certificates.</item>
/// </list>
/// Relative paths in <c>ConnectionStrings:Default</c> and <c>Anubis:SettingsFile</c> are
/// resolved against the data directory; absolute paths are used as they are.
/// Default data directory: the content root (the project folder when developing).
/// </summary>
public sealed class DataPaths
{
    public const string DataDirectoryKey = "Anubis:DataDirectory";
    public const string DatabaseFileName = "anubis.db";

    private DataPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        KeysDirectory = Path.Combine(dataDirectory, "keys");
    }

    public string DataDirectory { get; }

    /// <summary>Data Protection key ring and OpenIddict certificates.</summary>
    public string KeysDirectory { get; }

    public static DataPaths Resolve(IConfiguration configuration, string contentRoot)
    {
        var configured = configuration[DataDirectoryKey];
        var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? contentRoot : configured, contentRoot);
        var paths = new DataPaths(directory);
        Directory.CreateDirectory(paths.DataDirectory);
        Directory.CreateDirectory(paths.KeysDirectory);
        return paths;
    }

    /// <summary>A path inside the data directory (absolute paths stay unchanged).</summary>
    public string Resolve(string path) => Path.GetFullPath(path, DataDirectory);

    /// <summary>
    /// The SQLite connection string with its file inside the data directory:
    /// no connection string = <c>{data}/anubis.db</c>; a relative <c>Data Source</c> is relative to the data directory.
    /// </summary>
    public string SqliteConnectionString(string? configured)
    {
        var builder = new SqliteConnectionStringBuilder(string.IsNullOrWhiteSpace(configured) ? $"Data Source={DatabaseFileName}" : configured);
        var source = builder.DataSource;
        if (!string.IsNullOrEmpty(source) && source != ":memory:" && !source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            builder.DataSource = Resolve(source);
        }

        return builder.ToString();
    }
}
