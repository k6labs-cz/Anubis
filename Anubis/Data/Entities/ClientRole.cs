using System.ComponentModel.DataAnnotations;

namespace k6labs.Anubis.Data.Entities;

/// <summary>
/// A role a client application defines for itself ("Administrator" of the application "osiris"):
/// what a user may do there. Groups say who people are; roles are given to users and groups.
/// </summary>
public class ClientRole
{
    public const int NameMaxLength = 100;

    [Key]
    [MaxLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The application's entity id (not its client_id).</summary>
    [MaxLength(450)]
    public string ClientId { get; set; } = string.Empty;

    public Client? Client { get; set; }

    [MaxLength(NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }
}
