using System.Collections.Frozen;

using k6labs.Anubis.Options;

using Microsoft.EntityFrameworkCore;

namespace k6labs.Anubis.Tenancy;

/// <summary>
/// The tenants of this server, kept in memory (the catalog database is read at startup and after
/// every change), and the rule that finds the tenant of a host name:
/// <list type="number">
/// <item>a host name the tenant lists as its own (<see cref="Tenant.Hosts"/>),</item>
/// <item>the subdomain <c>{id}.{tenant domain}</c>; the tenant domain is <c>Anubis:TenantDomain</c>,
/// else the host of <c>Anubis:Issuer</c>, else <c>localhost</c>,</item>
/// <item>anything else is the default tenant.</item>
/// </list>
/// </summary>
public sealed class TenantCatalog
{
    public const string DomainKey = "Anubis:TenantDomain";

    private readonly IDbContextFactory<CatalogDbContext> _factory;
    private readonly IConfiguration _configuration;
    private volatile Snapshot _snapshot = new([]);

    public TenantCatalog(IDbContextFactory<CatalogDbContext> factory, IConfiguration configuration)
    {
        _factory = factory;
        _configuration = configuration;
    }

    /// <summary>The tenants in the catalog, by id: all but the default one.</summary>
    public IReadOnlyList<Tenant> Tenants => _snapshot.Tenants;

    /// <summary>The default tenant and the tenants in the catalog.</summary>
    public IEnumerable<Tenant> All => _snapshot.Tenants.Prepend(Tenant.Default);

    public Tenant? Find(string? id)
        => id == Tenant.DefaultId ? Tenant.Default : _snapshot.ById.GetValueOrDefault(id ?? string.Empty);

    /// <summary>The domain the tenants are subdomains of.</summary>
    public string Domain
    {
        get
        {
            if (NormalizeHost(_configuration[DomainKey]) is { Length: > 0 } configured)
            {
                return configured.TrimStart('.');
            }

            return DefaultHost ?? "localhost";
        }
    }

    /// <summary>The host of the server's <c>Anubis:Issuer</c>, the address of the default tenant; null without one.</summary>
    public string? DefaultHost
        => Uri.TryCreate(_configuration[$"{AnubisOptions.SectionName}:{nameof(AnubisOptions.Issuer)}"], UriKind.Absolute, out var uri)
            ? uri.Host.ToLowerInvariant()
            : null;

    /// <summary>The host name a tenant is published at: its first own host name, or its subdomain.</summary>
    public string PublicHost(Tenant tenant) => tenant.Hosts.FirstOrDefault() ?? $"{tenant.Id}.{Domain}";

    /// <summary>The tenant a request for <paramref name="host"/> (without the port) belongs to.</summary>
    public Tenant Resolve(string? host)
    {
        var snapshot = _snapshot;
        var name = NormalizeHost(host);
        if (name.Length == 0 || snapshot.Tenants.Count == 0)
        {
            return Tenant.Default;
        }

        if (snapshot.ByHost.TryGetValue(name, out var tenant))
        {
            return tenant;
        }

        var suffix = "." + Domain;
        return name.EndsWith(suffix, StringComparison.Ordinal) && snapshot.ById.TryGetValue(name[..^suffix.Length], out tenant)
            ? tenant
            : Tenant.Default;
    }

    /// <summary>Reads the catalog database again: at startup and after a tenant was added, changed or removed.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        _snapshot = new Snapshot(await db.Tenants.AsNoTracking().OrderBy(t => t.Id).ToListAsync(cancellationToken));
    }

    /// <summary>Lowercase, without the dot that may end a fully qualified name.</summary>
    public static string NormalizeHost(string? host) => (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private sealed class Snapshot(List<Tenant> tenants)
    {
        public IReadOnlyList<Tenant> Tenants { get; } = tenants;

        public FrozenDictionary<string, Tenant> ById { get; } = tenants.ToFrozenDictionary(t => t.Id, StringComparer.Ordinal);

        public FrozenDictionary<string, Tenant> ByHost { get; } = tenants
            .SelectMany(t => t.Hosts.Select(host => (Host: NormalizeHost(host), Tenant: t)))
            .DistinctBy(pair => pair.Host)
            .ToFrozenDictionary(pair => pair.Host, pair => pair.Tenant, StringComparer.Ordinal);
    }
}
