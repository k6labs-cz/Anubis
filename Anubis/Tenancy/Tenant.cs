using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace k6labs.Anubis.Tenancy;

/// <summary>
/// A tenant: an organization with a database of its own (users, groups, applications, roles, tokens,
/// history) and settings of its own. Nothing is shared between tenants but the server and its keys.
/// The tenant of a request is the one its host name belongs to (<see cref="TenantCatalog.Resolve"/>);
/// every other host is the <see cref="Default"/> tenant, which is not in the catalog and can't be deleted.
/// </summary>
public class Tenant
{
    public const string DefaultId = "default";

    public const int IdMaxLength = 63;

    public const int DisplayNameMaxLength = 200;

    /// <summary>The tenant every installation has: its database is <c>anubis.db</c> in the data directory.</summary>
    public static readonly Tenant Default = new() { Id = DefaultId };

    /// <summary>A DNS label (lowercase letters, digits, dashes): the folder of the tenant's data, its subdomain and the "tenant" claim.</summary>
    [Key]
    [MaxLength(IdMaxLength)]
    public string Id { get; set; } = string.Empty;

    [MaxLength(DisplayNameMaxLength)]
    public string? DisplayName { get; set; }

    /// <summary>Host names of its own (<c>login.acme.com</c>), besides the subdomain <c>{id}.{tenant domain}</c>. The first is the public one.</summary>
    public List<string> Hosts { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [NotMapped]
    public bool IsDefault => Id == DefaultId;
}
