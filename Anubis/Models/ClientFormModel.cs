using k6labs.Anubis.Localization;
using k6labs.Anubis.Resources;

namespace k6labs.Anubis.Models;

/// <summary>Form model used by the admin UI to create or edit an OIDC client.</summary>
public sealed class ClientFormModel
{
    [LRequired(nameof(Translations.Validation_ClientIdRequired))]
    [LRegularExpression(@"^[a-zA-Z0-9\-_\.]+$", nameof(Translations.Validation_Identifier))]
    public string ClientId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>Plain-text secret. Stored hashed by OpenIddict. Empty means a public client.</summary>
    [LMinLength(16, nameof(Translations.Validation_ClientSecretMinLength))]
    public string? ClientSecret { get; set; }

    /// <summary>One redirect URI per line.</summary>
    public string RedirectUrisText { get; set; } = string.Empty;

    /// <summary>One post-logout redirect URI per line.</summary>
    public string PostLogoutRedirectUrisText { get; set; } = string.Empty;

    public bool AllowAuthorizationCodeFlow { get; set; } = true;

    public bool AllowRefreshTokenFlow { get; set; } = true;

    public bool AllowClientCredentialsFlow { get; set; }

    public bool RequireConsent { get; set; } = true;

    /// <summary>Scopes the client may request.</summary>
    public List<string> Scopes { get; set; } = new();
}

/// <summary>Form model used by the admin UI to create a scope.</summary>
public sealed class ScopeFormModel
{
    [LRequired(nameof(Translations.Validation_ScopeNameRequired))]
    [LRegularExpression(@"^[a-zA-Z0-9\-_\.]+$", nameof(Translations.Validation_Identifier))]
    public string Name { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    /// <summary>Comma-separated resource (audience) names.</summary>
    public string ResourcesText { get; set; } = string.Empty;
}

/// <summary>Form model used by the admin UI to create a user.</summary>
public sealed class CreateUserModel
{
    [LRequired(nameof(Translations.Validation_EmailRequired))]
    [LEmailAddress]
    public string Email { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    [LRequired(nameof(Translations.Validation_PasswordRequired))]
    public string Password { get; set; } = string.Empty;

    /// <summary>Roles of the new account ("User" is always added).</summary>
    public HashSet<string> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum AvatarChange
{
    None,
    Replace,
    Remove,
}

/// <summary>One row of the claims editor.</summary>
public sealed class ClaimEntry
{
    public string Type { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class RoleEditModel
{
    public string Id { get; set; } = string.Empty;

    public string? ConcurrencyStamp { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Admin / User: cannot be renamed.</summary>
    public bool BuiltIn { get; set; }

    public List<ClaimEntry> Claims { get; set; } = new();
}

/// <summary>Admin edit of an existing account (all fields at once).</summary>
public sealed class UserEditModel
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Concurrency stamp when the form was opened: refuses to overwrite someone else's change.</summary>
    public string? ConcurrencyStamp { get; set; }

    /// <summary>Email address, which is also the sign-in name.</summary>
    [LRequired(nameof(Translations.Validation_EmailRequired))]
    [LEmailAddress]
    public string Email { get; set; } = string.Empty;

    public bool EmailConfirmed { get; set; }

    [LStringLength(100, nameof(Translations.Validation_DisplayNameTooLong))]
    public string? DisplayName { get; set; }

    public string? PhoneNumber { get; set; }

    public HashSet<string> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The user's own claims (the claims of their roles are added on top).</summary>
    public List<ClaimEntry> Claims { get; set; } = new();

    /// <summary>Replace / remove the avatar on save.</summary>
    public AvatarChange Avatar { get; set; }

    /// <summary>The new image (already resized WebP) when <see cref="Avatar"/> is <see cref="AvatarChange.Replace"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? AvatarImage { get; set; }
}
