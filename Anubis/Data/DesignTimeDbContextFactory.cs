using k6labs.Anubis.Data.Entities;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace k6labs.Anubis.Data;

/// <summary>
/// Used by the EF Core tools (dotnet ef) to create the context at design time
/// without starting the whole application.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AnubisDbContext>
{
    public AnubisDbContext CreateDbContext(string[] args)
    {
        // IdentityDbContext reads the schema version from IdentityOptions in the
        // application services; keep this in sync with Program.cs.
        var services = new ServiceCollection()
            .Configure<IdentityOptions>(identity => identity.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .BuildServiceProvider();

        var options = new DbContextOptionsBuilder<AnubisDbContext>()
            .UseSqlite("Data Source=anubis.db")
            .UseApplicationServiceProvider(services)
            .UseOpenIddict<Client, Authorization, Scope, Token, string, AnubisDbContext>()
            .Options;

        return new AnubisDbContext(options);
    }
}
