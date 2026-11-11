namespace k6labs.Anubis.Options;

/// <summary>
/// Strongly-typed application options, bound to the <c>Anubis</c> configuration section.
/// </summary>
public sealed class AnubisOptions
{
    public const string SectionName = "Anubis";

    /// <summary>Email of the administrator account created on first start.</summary>
    public string AdminEmail { get; set; } = "admin@anubis.local";

    /// <summary>
    /// Password of the administrator account. When empty, a strong random password is
    /// generated on first start and written to the log exactly once.
    /// </summary>
    public string? AdminPassword { get; set; }

    /// <summary>Whether self-service registration is enabled.</summary>
    public bool AllowRegistration { get; set; } = true;

    /// <summary>
    /// Users must confirm their email address (link sent by email) before they can sign in.
    /// Needs a working <c>Email</c> configuration. Accounts created by an admin are confirmed.
    /// </summary>
    public bool RequireConfirmedEmail { get; set; }

    /// <summary>
    /// Explicit issuer URI (e.g. <c>https://id.example.com</c>) used when running behind a
    /// reverse proxy. When empty, the issuer is derived from each request.
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>Name of the scope that grants access to the Anubis API (client credentials).</summary>
    public string ApiScope { get; set; } = "anubis-api";

    /// <summary>Resource (audience) associated with <see cref="ApiScope"/>.</summary>
    public string ApiResource { get; set; } = "anubis-api";

    // Token lifetimes: applied to every issued token (AuthorizationController), so the admin
    // "Tokens" page changes them without a restart.
    public int AccessTokenLifetimeMinutes { get; set; } = 60;

    public int IdentityTokenLifetimeMinutes { get; set; } = 20;

    public int AuthorizationCodeLifetimeMinutes { get; set; } = 10;

    public int RefreshTokenLifetimeDays { get; set; } = 14;

    /// <summary>Lifetime of the "consent granted" cookie used during the authorization code flow.</summary>
    public int ConsentCookieLifetimeMinutes { get; set; } = 10;

    /// <summary>OIDC clients created automatically on first start (idempotent).</summary>
    public List<SeedClientOptions> Clients { get; set; } = new();

    /// <summary>Tenants created automatically on first start (idempotent).</summary>
    public List<SeedTenantOptions> Tenants { get; set; } = new();
}

/// <summary>Configuration of a tenant seeded into the database.</summary>
public sealed class SeedTenantOptions
{
    /// <summary>Lowercase letters, digits and dashes.</summary>
    public string Id { get; set; } = string.Empty;

    public string? DisplayName { get; set; }
}

/// <summary>A role the seeded client defines for itself.</summary>
public sealed class SeedRoleOptions
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }
}

/// <summary>Configuration of a client application seeded into the database.</summary>
public sealed class SeedClientOptions
{
    public string ClientId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>Client secret in plain text (OpenIddict stores only its hash). Leave empty for a public client.</summary>
    public string? ClientSecret { get; set; }

    /// <summary><c>confidential</c> (default) or <c>public</c>.</summary>
    public string ClientType { get; set; } = "confidential";

    public List<string> RedirectUris { get; set; } = new();

    public List<string> PostLogoutRedirectUris { get; set; } = new();

    /// <summary>Subset of: authorization_code, refresh_token, client_credentials.</summary>
    public List<string> GrantTypes { get; set; } = new();

    /// <summary>Scopes the client is allowed to request (e.g. openid, profile, email, groups, offline_access).</summary>
    public List<string> Scopes { get; set; } = new();

    public bool RequireConsent { get; set; } = true;

    /// <summary>
    /// The application's roles (e.g. Administrator, User), added when missing even to an existing client.
    /// Allow the "roles" scope too, so the application gets the user's roles in its tokens.
    /// </summary>
    public List<SeedRoleOptions> Roles { get; set; } = new();
}
