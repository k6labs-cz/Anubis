using System.Security.Claims;

namespace k6labs.Anubis.Tenancy;

/// <summary>
/// The "tenant" claim: the id of the tenant that issued a token, in every access and ID token.
/// Applications read it next to "roles"; Anubis itself refuses a token presented to another tenant.
/// </summary>
public static class TenantClaims
{
    public const string Claim = "tenant";

    /// <summary>A token without the claim is from before tenants: it belongs to the default one.</summary>
    public static bool IsForCurrentTenant(ClaimsPrincipal principal)
        => (principal.FindFirst(Claim)?.Value ?? Tenant.DefaultId) == TenantContext.Current.Id;
}
