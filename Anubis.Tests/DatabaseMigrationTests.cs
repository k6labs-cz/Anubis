using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;

using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

public class DatabaseMigrationTests
{
    [Fact]
    public async Task TenantCatalog_Migrates()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var catalog = new k6labs.Anubis.Tenancy.CatalogDbContext(
            new DbContextOptionsBuilder<k6labs.Anubis.Tenancy.CatalogDbContext>().UseSqlite(connection).Options);

        await catalog.Database.MigrateAsync();
        Assert.False(catalog.Database.HasPendingModelChanges());

        catalog.Tenants.Add(new k6labs.Anubis.Tenancy.Tenant { Id = "acme", Hosts = ["login.acme.test", "sso.acme.test"] });
        await catalog.SaveChangesAsync();
        catalog.ChangeTracker.Clear();
        Assert.Equal(["login.acme.test", "sso.acme.test"], (await catalog.Tenants.SingleAsync()).Hosts);
    }

    /// <summary>
    /// A database from before tenants had databases of their own: its tenants were labels on role
    /// assignments. A role limited to one tenant must not become a role everywhere.
    /// </summary>
    [Fact]
    public async Task Upgrade_DropsTheTenantLabels_AndTheAssignmentsLimitedToOneTenant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var services = new ServiceCollection()
            .Configure<IdentityOptions>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .BuildServiceProvider();
        var options = new DbContextOptionsBuilder<AnubisDbContext>()
            .UseSqlite(connection)
            .UseApplicationServiceProvider(services)
            .UseOpenIddict<Client, Authorization, Scope, Token, string, AnubisDbContext>()
            .Options;
        await using var context = new AnubisDbContext(options);

        await context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("TenantsAndRoles");
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Applications" ("Id", "ClientId", "RequireConsent") VALUES ('app', 'osiris', 0);
            INSERT INTO "ClientRoles" ("Id", "ClientId", "Name") VALUES ('role', 'app', 'Administrator');
            INSERT INTO "Groups" ("Id", "Name", "NormalizedName") VALUES ('group', 'Staff', 'STAFF');
            INSERT INTO "Tenants" ("Id", "CreatedAt") VALUES ('acme', '2026-11-11 00:00:00+00:00');
            INSERT INTO "RoleAssignments" ("Id", "RoleId", "GroupId", "TenantId", "CreatedAt") VALUES ('everywhere', 'role', 'group', NULL, '2026-11-11 00:00:00+00:00');
            INSERT INTO "RoleAssignments" ("Id", "RoleId", "GroupId", "TenantId", "CreatedAt") VALUES ('in-acme', 'role', 'group', 'acme', '2026-11-11 00:00:00+00:00');
            """);

        await context.Database.MigrateAsync();

        Assert.Equal("everywhere", (await context.RoleAssignments.SingleAsync()).Id);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Tenants'";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Migrate_CreatesCompleteSchema_AndPreservesDataWhenRepeated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var services = new ServiceCollection()
            .Configure<IdentityOptions>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .BuildServiceProvider();
        var options = new DbContextOptionsBuilder<AnubisDbContext>()
            .UseSqlite(connection)
            .UseApplicationServiceProvider(services)
            .UseOpenIddict<Client, Authorization, Scope, Token, string, AnubisDbContext>()
            .Options;
        await using var context = new AnubisDbContext(options);

        await context.Database.MigrateAsync();

        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.False(await context.Roles.AnyAsync());

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var tables = new HashSet<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }
        foreach (var table in new[] { "Groups", "Users", "UserGroups", "GroupClaims", "UserPasskeys", "UserAvatars", "UserEvents", "Applications", "Scopes", "Authorizations", "Tokens" })
        {
            Assert.Contains(table, tables);
        }

        context.Roles.Add(new Group { Name = "Existing group", NormalizedName = "EXISTING GROUP" });
        await context.SaveChangesAsync();

        await context.Database.MigrateAsync();

        Assert.Equal("Existing group", (await context.Roles.SingleAsync()).Name);
    }
}
