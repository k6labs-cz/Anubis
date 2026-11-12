using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Options;
using k6labs.Anubis.Services;
using k6labs.Anubis.Tenancy;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using OpenIddict.Abstractions;

namespace k6labs.Anubis.Data;

/// <summary>What is put into a tenant's database besides the baseline: its administrator and its clients with their roles.</summary>
public sealed record TenantSeed(string? AdminEmail, string? AdminPassword, IReadOnlyList<SeedClientOptions> Clients)
{
    public static readonly TenantSeed None = new(null, null, []);
}

/// <summary>The seed can't be applied (e.g. the administrator's password breaks the password rules).</summary>
public sealed class TenantSeedException(string message) : Exception(message);

/// <summary>
/// On startup: migrates the tenant catalog, creates the tenants named in the configuration, and for
/// every tenant applies the EF Core migrations to its database and seeds the baseline data (groups,
/// admin user, API scope, configured clients and their roles). Seeding is idempotent.
/// </summary>
public static class DatabaseInitializer
{
    public const string AdminGroup = "Admins";
    public const string UserGroup = "Users";

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        // One instance at a time: two starting together on the same data directory would both
        // create the tenants and the accounts they find missing.
        await using var startup = await LockAsync(services.GetRequiredService<Hosting.DataPaths>(), cancellationToken);

        var options = services.GetRequiredService<IConfiguration>().GetSection(AnubisOptions.SectionName).Get<AnubisOptions>() ?? new AnubisOptions();
        var catalog = services.GetRequiredService<TenantCatalog>();
        var tenants = services.GetRequiredService<TenantAdministration>();

        await using (var db = await services.GetRequiredService<IDbContextFactory<CatalogDbContext>>().CreateDbContextAsync(cancellationToken))
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        await catalog.ReloadAsync(cancellationToken);

        // The default tenant: the database every installation has had, seeded from the "Anubis" section.
        await InitializeTenantAsync(services, Tenant.Default, new TenantSeed(options.AdminEmail, options.AdminPassword, options.Clients), cancellationToken);

        var configured = options.Tenants
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .GroupBy(t => t.Id.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        // Tenants in the catalog: schema changes of a new version, and what the configuration adds to them.
        foreach (var tenant in catalog.Tenants)
        {
            var seed = configured.TryGetValue(tenant.Id, out var known) ? Seed(known) : TenantSeed.None;
            await InitializeTenantAsync(services, tenant, seed, cancellationToken);
        }

        // Tenants the configuration names for the first time.
        foreach (var (id, tenant) in configured.Where(pair => catalog.Find(pair.Key) is null))
        {
            await tenants.CreateAsync(id, tenant.DisplayName, tenant.Hosts, administrator: null, Seed(tenant), cancellationToken);
        }
    }

    /// <summary>Holds a lock file in the data directory; waits (up to two minutes) while another instance holds it.</summary>
    private static async Task<FileStream> LockAsync(Hosting.DataPaths paths, CancellationToken cancellationToken)
    {
        var path = Path.Combine(paths.DataDirectory, ".startup.lock");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < 600)
            {
                await Task.Delay(200, cancellationToken);
            }
        }
    }

    /// <summary>Brings one tenant's database up to date: its schema, the built-in groups, the API scope, and the seed.</summary>
    public static async Task InitializeTenantAsync(IServiceProvider services, Tenant tenant, TenantSeed seed, CancellationToken cancellationToken = default)
    {
        if (!tenant.IsDefault)
        {
            Directory.CreateDirectory(services.GetRequiredService<Hosting.DataPaths>().TenantDirectory(tenant.Id));
        }

        await using var scope = services.CreateTenantScope(tenant);
        var provider = scope.ServiceProvider;

        var context = provider.GetRequiredService<AnubisDbContext>();
        await context.Database.MigrateAsync(cancellationToken);

        var options = provider.GetRequiredService<IOptions<AnubisOptions>>().Value;

        await EnsureGroupsAsync(provider);
        await EnsureAdminUserAsync(provider, seed);
        await EnsureApiScopeAsync(provider, options);
        await EnsureClientsAsync(provider, seed.Clients);
        await EnsureRolesAsync(provider, seed.Clients);
    }

    private static TenantSeed Seed(SeedTenantOptions tenant) => new(tenant.AdminEmail, tenant.AdminPassword, tenant.Clients);

    private static async Task EnsureRolesAsync(IServiceProvider provider, IReadOnlyList<SeedClientOptions> clients)
    {
        var roles = provider.GetRequiredService<RoleAdministration>();

        foreach (var client in clients.Where(c => !string.IsNullOrWhiteSpace(c.ClientId) && c.Roles.Count > 0))
        {
            var existing = (await roles.ListRolesAsync(client.ClientId)).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var role in client.Roles.Where(r => !string.IsNullOrWhiteSpace(r.Name) && !existing.Contains(r.Name.Trim())))
            {
                await roles.CreateRoleAsync(client.ClientId, role.Name, role.Description);
            }
        }
    }

    private static async Task EnsureGroupsAsync(IServiceProvider provider)
    {
        var groupManager = provider.GetRequiredService<RoleManager<Group>>();

        foreach (var group in new[] { AdminGroup, UserGroup })
        {
            if (!await groupManager.RoleExistsAsync(group))
            {
                await groupManager.CreateAsync(new Group(group));
            }
        }
    }

    private static async Task EnsureAdminUserAsync(IServiceProvider provider, TenantSeed options)
    {
        if (string.IsNullOrWhiteSpace(options.AdminEmail) || string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            return;
        }

        var userManager = provider.GetRequiredService<UserManager<User>>();

        var admin = await userManager.FindByEmailAsync(options.AdminEmail);
        if (admin is not null)
        {
            return;
        }

        admin = new User
        {
            UserName = options.AdminEmail,
            Email = options.AdminEmail,
            EmailConfirmed = true,
            DisplayName = "Administrator",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var result = await userManager.CreateAsync(admin, options.AdminPassword);
        if (!result.Succeeded)
        {
            throw new TenantSeedException(
                "Failed to create the admin user: " + string.Join(", ", result.Errors.Select(error => error.Description)));
        }

        if (!await userManager.IsInRoleAsync(admin, AdminGroup))
        {
            await userManager.AddToRoleAsync(admin, AdminGroup);
        }
    }

    private static async Task EnsureApiScopeAsync(IServiceProvider provider, AnubisOptions options)
    {
        var scopeManager = provider.GetRequiredService<IOpenIddictScopeManager>();

        if (await scopeManager.FindByNameAsync(options.ApiScope) is not null)
        {
            return;
        }

        await scopeManager.CreateAsync(new OpenIddictScopeDescriptor
        {
            Name = options.ApiScope,
            DisplayName = "Anubis API",
            Description = "Access to the Anubis API (client credentials).",
            Resources = { options.ApiResource },
        });
    }

    private static async Task EnsureClientsAsync(IServiceProvider provider, IReadOnlyList<SeedClientOptions> clients)
    {
        var applicationManager = provider.GetRequiredService<IOpenIddictApplicationManager>();

        foreach (var client in clients)
        {
            if (string.IsNullOrWhiteSpace(client.ClientId))
            {
                continue;
            }

            if (await applicationManager.FindByClientIdAsync(client.ClientId) is not null)
            {
                continue;
            }

            var application = await applicationManager.CreateAsync(OidcClientDescriptorFactory.Create(client));

            if (application is Client entity)
            {
                entity.RequireConsent = client.RequireConsent;
                await applicationManager.UpdateAsync(entity);
            }
        }
    }
}
