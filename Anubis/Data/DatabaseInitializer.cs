using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Options;
using k6labs.Anubis.Services;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using OpenIddict.Abstractions;

namespace k6labs.Anubis.Data;

/// <summary>
/// Applies EF Core migrations and seeds the baseline data (roles, admin user,
/// API scope, configured clients) on startup. Seeding is idempotent.
/// </summary>
public static class DatabaseInitializer
{
    public const string AdminRole = "Admin";
    public const string UserRole = "User";

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var context = provider.GetRequiredService<AnubisDbContext>();
        await context.Database.MigrateAsync(cancellationToken);

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseInitializer));
        var options = provider.GetRequiredService<IOptions<AnubisOptions>>().Value;

        await EnsureRolesAsync(provider);
        await EnsureAdminUserAsync(provider, options, logger);
        await EnsureApiScopeAsync(provider, options);
        await EnsureClientsAsync(provider, options);
    }

    private static async Task EnsureRolesAsync(IServiceProvider provider)
    {
        var roleManager = provider.GetRequiredService<RoleManager<Role>>();

        foreach (var role in new[] { AdminRole, UserRole })
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new Role(role));
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

        if (!await userManager.IsInRoleAsync(admin, AdminRole))
        {
            await userManager.AddToRoleAsync(admin, AdminRole);
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
