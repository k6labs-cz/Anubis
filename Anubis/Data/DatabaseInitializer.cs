using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Options;
using k6labs.Anubis.Services;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using OpenIddict.Abstractions;

namespace k6labs.Anubis.Data;

/// <summary>
/// Applies EF Core migrations and seeds the baseline data (groups, admin user,
/// API scope, configured clients) on startup. Seeding is idempotent.
/// </summary>
public static class DatabaseInitializer
{
    public const string AdminGroup = "Admins";
    public const string UserGroup = "Users";

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var context = provider.GetRequiredService<AnubisDbContext>();
        await context.Database.MigrateAsync(cancellationToken);

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseInitializer));
        var options = provider.GetRequiredService<IOptions<AnubisOptions>>().Value;

        await EnsureGroupsAsync(provider);
        await EnsureAdminUserAsync(provider, options, logger);
        await EnsureApiScopeAsync(provider, options);
        await EnsureClientsAsync(provider, options);
        await EnsureTenantsAndRolesAsync(provider, options);
    }

    private static async Task EnsureTenantsAndRolesAsync(IServiceProvider provider, AnubisOptions options)
    {
        var roles = provider.GetRequiredService<RoleAdministration>();

        var tenants = (await roles.ListTenantsAsync()).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var tenant in options.Tenants.Where(t => !string.IsNullOrWhiteSpace(t.Id) && !tenants.Contains(t.Id.Trim())))
        {
            await roles.CreateTenantAsync(tenant.Id, tenant.DisplayName);
        }

        foreach (var client in options.Clients.Where(c => !string.IsNullOrWhiteSpace(c.ClientId) && c.Roles.Count > 0))
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

    private static async Task EnsureAdminUserAsync(IServiceProvider provider, AnubisOptions options, ILogger logger)
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
            throw new InvalidOperationException(
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

    private static async Task EnsureClientsAsync(IServiceProvider provider, AnubisOptions options)
    {
        var applicationManager = provider.GetRequiredService<IOpenIddictApplicationManager>();

        foreach (var client in options.Clients)
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
