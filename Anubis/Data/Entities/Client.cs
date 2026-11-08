using OpenIddict.EntityFrameworkCore.Models;

namespace k6labs.Anubis.Data.Entities;

/// <summary>OpenIddict application (client) with Anubis-specific metadata.</summary>
public class Client : OpenIddictEntityFrameworkCoreApplication<string, Authorization, Token>
{
    /// <summary>Whether the user must explicitly consent before tokens are issued for this client.</summary>
    public bool RequireConsent { get; set; } = true;
}
