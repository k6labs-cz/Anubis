using System.ComponentModel.DataAnnotations;

namespace k6labs.Anubis.Data.Entities;

/// <summary>
/// A role given to a user or to a group (exactly one of the two), in one tenant or, without a tenant,
/// in all of them. A group's members have the group's roles.
/// </summary>
public class RoleAssignment
{
    [Key]
    [MaxLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [MaxLength(36)]
    public string RoleId { get; set; } = string.Empty;

    public ClientRole? Role { get; set; }

    [MaxLength(450)]
    public string? UserId { get; set; }

    public User? User { get; set; }

    [MaxLength(450)]
    public string? GroupId { get; set; }

    public Group? Group { get; set; }

    /// <summary>Null: the role holds in every tenant.</summary>
    [MaxLength(Tenant.IdMaxLength)]
    public string? TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
