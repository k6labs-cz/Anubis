using System.ComponentModel.DataAnnotations;

namespace k6labs.Anubis.Data.Entities;

/// <summary>
/// A tenant: an organization (a customer, a team, a workspace) shared by all client applications.
/// A user belongs to a tenant by having a role of some application in it (<see cref="RoleAssignment"/>).
/// </summary>
public class Tenant
{
    public const int IdMaxLength = 63;

    /// <summary>Lowercase letters, digits and dashes; what applications see in the "roles" claim.</summary>
    [Key]
    [MaxLength(IdMaxLength)]
    public string Id { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
