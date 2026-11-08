using k6labs.Anubis.Data.Entities;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace k6labs.Anubis.Data;

/// <summary>
/// SQLite database context combining ASP.NET Core Identity and the OpenIddict entities.
/// OpenIddict entities are registered through
/// <c>DbContextOptionsBuilder.UseOpenIddict&lt;...&gt;()</c> in <c>Program.cs</c>.
/// </summary>
public class AnubisDbContext : IdentityDbContext<User, Group, string>
{
    // Default table-name prefixes stripped from Identity ("AspNetUsers" -> "Users")
    // and OpenIddict ("OpenIddictTokens" -> "Tokens") tables.
    private static readonly string[] TablePrefixes = ["AspNet", "OpenIddict"];

    public AnubisDbContext(DbContextOptions<AnubisDbContext> options)
        : base(options)
    {
    }

    public DbSet<UserAvatar> UserAvatars => Set<UserAvatar>();

    public DbSet<UserEvent> UserEvents => Set<UserEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<UserAvatar>(entity =>
        {
            entity.ToTable("UserAvatars");
            entity.HasKey(a => a.UserId);
            entity.HasOne(a => a.User)
                  .WithOne()
                  .HasForeignKey<UserAvatar>(a => a.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserEvent>(entity =>
        {
            entity.ToTable("UserEvents");
            // SQLite returns DateTime without a kind; the value is always UTC.
            entity.Property(e => e.OccurredAtUtc)
                  .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
            entity.HasIndex(e => new { e.UserId, e.Id });
            entity.HasIndex(e => e.OccurredAtUtc);
            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is null)
            {
                continue;
            }

            foreach (var prefix in TablePrefixes)
            {
                if (tableName.StartsWith(prefix, StringComparison.Ordinal) && tableName.Length > prefix.Length)
                {
                    entityType.SetTableName(tableName[prefix.Length..]);
                    break;
                }
            }
        }

        // Identity roles are Anubis groups.
        builder.Entity<Group>(entity =>
        {
            entity.ToTable("Groups");
            entity.HasIndex(g => g.NormalizedName).HasDatabaseName("GroupNameIndex");
        });
        builder.Entity<IdentityUserRole<string>>(entity =>
        {
            entity.ToTable("UserGroups");
            entity.Property(m => m.RoleId).HasColumnName("GroupId");
        });
        builder.Entity<IdentityRoleClaim<string>>(entity =>
        {
            entity.ToTable("GroupClaims");
            entity.Property(c => c.RoleId).HasColumnName("GroupId");
        });
    }
}
