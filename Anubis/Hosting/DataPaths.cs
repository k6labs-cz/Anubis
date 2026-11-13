using Microsoft.Data.Sqlite;

namespace k6labs.Anubis.Hosting;

/// <summary>
/// Where Anubis writes its state. Everything lives in one data directory
/// (<c>Anubis:DataDirectory</c>, in a container e.g. <c>/data</c> on a persistent volume):
/// <list type="bullet">
/// <item><c>anubis.db</c> (+ <c>-wal</c>/<c>-shm</c>): the SQLite database of the default tenant,</item>
/// <item><c>anubis.settings.json</c>: settings of the default tenant saved from the admin UI,</item>
/// <item><c>anubis.tenants.db</c>: the catalog of the other tenants and their host names,</item>
/// <item><c>tenants/{id}/</c>: one folder per tenant with its own <c>anubis.db</c>, <c>anubis.settings.json</c> and <c>keys/</c> (its OpenIddict certificates),</item>
/// <item><c>keys/</c>: ASP.NET Core Data Protection keys (cookies, antiforgery, email links)
/// and the OpenIddict signing / encryption certificates.</item>
/// </list>
/// Relative paths in <c>ConnectionStrings:Default</c>, <c>ConnectionStrings:Tenants</c> (the catalog)
/// and <c>Anubis:SettingsFile</c> are resolved against the data directory; absolute paths are used as they are.
/// Default data directory: the content root (the project folder when developing).
/// </summary>
public sealed class DataPaths
{
    public const string DataDirectoryKey = "Anubis:DataDirectory";
    public const string DatabaseFileName = "anubis.db";
    public const string CatalogFileName = "anubis.tenants.db";

    private DataPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        KeysDirectory = Path.Combine(dataDirectory, "keys");
        TenantsDirectory = Path.Combine(dataDirectory, "tenants");
    }

    public string DataDirectory { get; }

    /// <summary>Data Protection key ring and OpenIddict certificates.</summary>
    public string KeysDirectory { get; }

    /// <summary>The folders of the tenants (all but the default one, whose files are in the data directory itself).</summary>
    public string TenantsDirectory { get; }

    /// <summary>The folder with a tenant's database and settings. The id is a DNS label, checked when the tenant is created.</summary>
    public string TenantDirectory(string tenantId) => Path.Combine(TenantsDirectory, tenantId);

    /// <summary>The tenant's own OpenIddict signing / encryption certificates.</summary>
    public string TenantKeysDirectory(string tenantId) => Path.Combine(TenantDirectory(tenantId), "keys");

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
    public string SqliteConnectionString(string? configured, string defaultFileName = DatabaseFileName)
    {
        var builder = new SqliteConnectionStringBuilder(string.IsNullOrWhiteSpace(configured) ? $"Data Source={defaultFileName}" : configured);
        var source = builder.DataSource;
        if (!string.IsNullOrEmpty(source) && source != ":memory:" && !source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            builder.DataSource = Resolve(source);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The connection string of a tenant's database: the configured one (<c>ConnectionStrings:Default</c>)
    /// for the default tenant, and the same with the file <c>tenants/{id}/anubis.db</c> for any other.
    /// </summary>
    public string TenantConnectionString(Tenancy.Tenant tenant, string? configured)
    {
        var connectionString = SqliteConnectionString(configured);
        return tenant.IsDefault
            ? connectionString
            : new SqliteConnectionStringBuilder(connectionString) { DataSource = Path.Combine(TenantDirectory(tenant.Id), DatabaseFileName) }.ToString();
    }

    /// <summary>The connection string of the tenant catalog (<c>ConnectionStrings:Tenants</c>, default <c>{data}/anubis.tenants.db</c>).</summary>
    public string CatalogConnectionString(string? configured) => SqliteConnectionString(configured, CatalogFileName);
}
