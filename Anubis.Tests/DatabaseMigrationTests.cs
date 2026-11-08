using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;

using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Anubis.Tests;

public class DatabaseMigrationTests
{
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
        foreach (var table in new[] { "Roles", "Users", "UserRoles", "UserPasskeys", "UserAvatars", "UserEvents", "Applications", "Scopes", "Authorizations", "Tokens" })
        {
            Assert.Contains(table, tables);
        }

        context.Roles.Add(new Role { Name = "Existing role", NormalizedName = "EXISTING ROLE" });
        await context.SaveChangesAsync();

        await context.Database.MigrateAsync();

        Assert.Equal("Existing role", (await context.Roles.SingleAsync()).Name);
    }
}
