using Microsoft.EntityFrameworkCore;

namespace k6labs.Anubis.Tenancy;

/// <summary>
/// The catalog of tenants (<c>anubis.tenants.db</c> in the data directory): which tenants exist and
/// which host names are theirs. Everything else about a tenant is in the tenant's own database.
/// </summary>
public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options)
        : base(options)
    {
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Tenant>().ToTable("Tenants");
}
