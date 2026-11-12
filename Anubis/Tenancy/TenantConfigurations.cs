using System.Collections.Concurrent;

using k6labs.Anubis.Hosting;
using k6labs.Anubis.Options;
using k6labs.Anubis.Services;

using Microsoft.Extensions.Primitives;

namespace k6labs.Anubis.Tenancy;

/// <summary>
/// The configuration of each tenant: the server's configuration (appsettings, mounted files,
/// environment variables), which is the same for everyone, and on top of it the tenant's own settings
/// file written by the admin pages. The options the application reads (<see cref="TenantOptions"/>)
/// are bound from the configuration of the current tenant.
/// <para>
/// A tenant other than the default one does not inherit <c>Anubis:Issuer</c> (it is the address of the
/// default tenant): when the server has one, the tenant's is the same address with the tenant's public
/// host name; without one, it comes from each request, as for the default tenant. The tenant's display
/// name is the default name of its server. Both can be changed on the tenant's "General" page.
/// </para>
/// </summary>
public sealed class TenantConfigurations : IDisposable
{
    private readonly IConfiguration _server;
    private readonly DataPaths _paths;
    private readonly TenantCatalog _catalog;
    private readonly ConcurrentDictionary<string, Lazy<Entry>> _entries = new(StringComparer.Ordinal);
    private event Action? Changed;

    public TenantConfigurations(IConfiguration server, DataPaths paths, TenantCatalog catalog)
    {
        _server = server;
        _paths = paths;
        _catalog = catalog;
    }

    /// <summary>The configuration of the current tenant.</summary>
    public IConfiguration Current => For(TenantContext.Current).Configuration;

    /// <summary>The settings file of the current tenant.</summary>
    public SettingsFile CurrentSettings => For(TenantContext.Current).Settings;

    public IConfiguration Get(Tenant tenant) => For(tenant).Configuration;

    /// <summary>Where a tenant's settings file is: <c>Anubis:SettingsFile</c> for the default tenant, the tenant's folder otherwise.</summary>
    public string SettingsPath(Tenant tenant)
        => tenant.IsDefault
            ? SettingsFile.ResolvePath(_server, _paths.DataDirectory)
            : Path.Combine(_paths.TenantDirectory(tenant.Id), SettingsFile.DefaultFileName);

    /// <summary>Runs <paramref name="listener"/> whenever the configuration of any tenant changed (a saved settings file, a reloaded source).</summary>
    public IDisposable OnChange(Action listener)
    {
        Changed += listener;
        return new Subscription(this, listener);
    }

    /// <summary>Drops what is known about a tenant that was changed or deleted; it is read again when needed.</summary>
    public void Forget(string tenantId)
    {
        if (_entries.TryRemove(tenantId, out var entry) && entry.IsValueCreated)
        {
            entry.Value.Dispose();
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values.Where(entry => entry.IsValueCreated))
        {
            entry.Value.Dispose();
        }

        _entries.Clear();
    }

    // Built from the tenant as the catalog knows it now: the caller's copy may be from before a change
    // (an interactive page keeps the one it started with).
    private Entry For(Tenant tenant)
        => _entries.GetOrAdd(tenant.Id, _ => new Lazy<Entry>(() => Create(_catalog.Find(tenant.Id) ?? tenant))).Value;

    private Entry Create(Tenant tenant)
    {
        var builder = new ConfigurationBuilder().AddConfiguration(_server);
        if (!tenant.IsDefault)
        {
            builder.AddInMemoryCollection(Overrides(tenant));
        }

        // Only the default tenant's file is watched (it may be edited by hand, as before); the others
        // change through the admin pages, which reload the configuration themselves. A watcher per
        // tenant would run into the operating system's limit of file watchers.
        var path = SettingsPath(tenant);
        builder.AddJsonFile(path, optional: true, reloadOnChange: tenant.IsDefault);

        var root = builder.Build();
        var subscription = ChangeToken.OnChange(root.GetReloadToken, () => Changed?.Invoke());
        return new Entry(root, new SettingsFile(path, root), subscription);
    }

    private Dictionary<string, string?> Overrides(Tenant tenant)
    {
        var values = new Dictionary<string, string?>
        {
            [$"{AnubisOptions.SectionName}:{nameof(AnubisOptions.Issuer)}"] = string.Empty,
        };

        var issuer = _server[$"{AnubisOptions.SectionName}:{nameof(AnubisOptions.Issuer)}"];
        if (Uri.TryCreate(issuer, UriKind.Absolute, out var uri))
        {
            values[$"{AnubisOptions.SectionName}:{nameof(AnubisOptions.Issuer)}"] =
                new UriBuilder(uri) { Host = _catalog.PublicHost(tenant) }.Uri.AbsoluteUri.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(tenant.DisplayName))
        {
            values[$"{GeneralOptions.SectionName}:{nameof(GeneralOptions.ServerName)}"] =
                tenant.DisplayName.Length > GeneralOptions.ServerNameMaxLength ? tenant.DisplayName[..GeneralOptions.ServerNameMaxLength] : tenant.DisplayName;
        }

        return values;
    }

    private sealed record Entry(IConfigurationRoot Configuration, SettingsFile Settings, IDisposable Subscription) : IDisposable
    {
        public void Dispose()
        {
            Subscription.Dispose();
            (Configuration as IDisposable)?.Dispose();
        }
    }

    private sealed class Subscription(TenantConfigurations owner, Action listener) : IDisposable
    {
        public void Dispose() => owner.Changed -= listener;
    }
}
