using System.Text.RegularExpressions;

using k6labs.Anubis.Data;
using k6labs.Anubis.Hosting;
using k6labs.Anubis.Resources;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace k6labs.Anubis.Tenancy;

public enum TenantError
{
    Invalid,
    Conflict,
    NotFound,
}

/// <summary>A change to the tenants that can't be made; the message is for the administrator.</summary>
public sealed class TenantAdministrationException(TenantError error, string message) : Exception(message)
{
    public TenantError Error { get; } = error;
}

/// <summary>The first administrator of a new tenant.</summary>
public sealed record TenantAdministrator(string Email, string Password);

/// <summary>
/// Creates, changes and deletes tenants: the admin page and the SCIM API of the default tenant, and the
/// seeding at startup. Creating a tenant makes its folder and its database (schema, built-in groups, API
/// scope, optionally its first administrator); deleting one removes them for good.
/// </summary>
public sealed partial class TenantAdministration
{
    private readonly IDbContextFactory<CatalogDbContext> _factory;
    private readonly TenantCatalog _catalog;
    private readonly TenantConfigurations _configurations;
    private readonly DataPaths _paths;
    private readonly IServiceProvider _services;
    private readonly ILogger<TenantAdministration> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TenantAdministration(
        IDbContextFactory<CatalogDbContext> factory,
        TenantCatalog catalog,
        TenantConfigurations configurations,
        DataPaths paths,
        IServiceProvider services,
        ILogger<TenantAdministration> logger)
    {
        _factory = factory;
        _catalog = catalog;
        _configurations = configurations;
        _paths = paths;
        _services = services;
        _logger = logger;
    }

    /// <summary>The default tenant first, then the others by id.</summary>
    public IReadOnlyList<Tenant> List() => _catalog.All.ToList();

    public Tenant? Find(string? id) => _catalog.Find(id);

    /// <summary>The host name a tenant is reached at when it has none of its own.</summary>
    public string Subdomain(Tenant tenant) => $"{tenant.Id}.{_catalog.Domain}";

    /// <summary>
    /// Creates the tenant and its database. With <paramref name="administrator"/> the tenant gets its first
    /// administrator; without one, the first account registered in the tenant becomes it.
    /// </summary>
    public async Task<Tenant> CreateAsync(
        string? id, string? displayName, IEnumerable<string>? hosts, TenantAdministrator? administrator,
        TenantSeed? seed = null, CancellationToken cancellationToken = default)
    {
        id = (id ?? string.Empty).Trim();
        if (!IdPattern().IsMatch(id) || id == Tenant.DefaultId)
        {
            throw new TenantAdministrationException(TenantError.Invalid, Translations.Error_TenantIdInvalid);
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_catalog.Find(id) is not null)
            {
                throw new TenantAdministrationException(TenantError.Conflict, Translations.Error_TenantExists.F(id));
            }

            var tenant = new Tenant { Id = id, DisplayName = Clean(displayName), Hosts = CheckHosts(hosts, id) };

            await using (var db = await _factory.CreateDbContextAsync(cancellationToken))
            {
                // The catalog may know more than this instance does (another one created the tenant just now).
                if (await db.Tenants.AnyAsync(t => t.Id == id, cancellationToken))
                {
                    await _catalog.ReloadAsync(cancellationToken);
                    throw new TenantAdministrationException(TenantError.Conflict, Translations.Error_TenantExists.F(id));
                }

                db.Tenants.Add(tenant);
                await db.SaveChangesAsync(cancellationToken);
            }

            try
            {
                // A folder left behind by a tenant of the same name (a failed delete) must not become this tenant's data.
                DeleteFiles(id);
                Directory.CreateDirectory(_paths.TenantDirectory(id));

                seed = (seed ?? TenantSeed.None) with
                {
                    AdminEmail = administrator?.Email ?? seed?.AdminEmail,
                    AdminPassword = administrator?.Password ?? seed?.AdminPassword,
                };
                await DatabaseInitializer.InitializeTenantAsync(_services, tenant, seed, cancellationToken);
            }
            catch (Exception exception)
            {
                await RemoveAsync(id, CancellationToken.None);
                throw exception is TenantSeedException
                    ? new TenantAdministrationException(TenantError.Invalid, exception.Message)
                    : exception;
            }

            await _catalog.ReloadAsync(cancellationToken);
            _logger.LogInformation("Created the tenant {Tenant}.", id);
            return _catalog.Find(id)!;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A tenant's id is its folder, its subdomain and its claim, so only the display name and the host names change.</summary>
    public async Task<Tenant> UpdateAsync(string id, string? displayName, IEnumerable<string>? hosts, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var tenant = await db.Tenants.FindAsync([id], cancellationToken) ?? throw NotFound(id);
            tenant.DisplayName = Clean(displayName);
            tenant.Hosts = CheckHosts(hosts, id);
            await db.SaveChangesAsync(cancellationToken);

            await _catalog.ReloadAsync(cancellationToken);
            _configurations.Forget(id);   // its address and the default name of its server may have changed
            return _catalog.Find(id)!;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Deletes the tenant with its database and settings. The default tenant can't be deleted.</summary>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_catalog.Find(id) is not { IsDefault: false })
            {
                throw NotFound(id);
            }

            await RemoveAsync(id, cancellationToken);
            _logger.LogInformation("Deleted the tenant {Tenant}.", id);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken))
        {
            await db.Tenants.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken);
        }

        // From here on no request is resolved to the tenant; then its files can go.
        await _catalog.ReloadAsync(cancellationToken);
        _configurations.Forget(id);
        DeleteFiles(id);
    }

    private void DeleteFiles(string id)
    {
        var directory = _paths.TenantDirectory(id);
        if (!Directory.Exists(directory))
        {
            return;
        }

        // Pooled connections keep the database file open.
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    /// <summary>Host names of the tenant: valid, lowercase, without duplicates, and nobody else's.</summary>
    private List<string> CheckHosts(IEnumerable<string>? hosts, string tenantId)
    {
        var result = new List<string>();
        foreach (var host in (hosts ?? []).Select(TenantCatalog.NormalizeHost).Where(host => host.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (host.Length > 253 || !HostPattern().IsMatch(host))
            {
                throw new TenantAdministrationException(TenantError.Invalid, Translations.Error_TenantHostInvalid.F(host));
            }

            // The default tenant's address, another tenant's own host name, or the subdomain another tenant is reached at.
            var owner = _catalog.Resolve(host);
            if (host == _catalog.DefaultHost || (!owner.IsDefault && owner.Id != tenantId))
            {
                throw new TenantAdministrationException(TenantError.Conflict, Translations.Error_TenantHostTaken.F(host));
            }

            result.Add(host);
        }

        return result;
    }

    private static TenantAdministrationException NotFound(string? id)
        => new(TenantError.NotFound, Translations.Error_RoleItemNotFound.F(id));

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim() is { Length: > Tenant.DisplayNameMaxLength } text ? text[..Tenant.DisplayNameMaxLength] : value.Trim();

    /// <summary>A DNS label: a folder name, a subdomain and the "tenant" claim.</summary>
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex IdPattern();

    /// <summary>A host name without a port: DNS labels separated by dots.</summary>
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex HostPattern();
}
