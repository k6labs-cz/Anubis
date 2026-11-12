using k6labs.Anubis.Data;

using Microsoft.EntityFrameworkCore;

namespace k6labs.Anubis.Identity;

/// <summary>
/// The user's roles in the application that asks for the "roles" scope, in the access token, the ID token,
/// userinfo and introspection: the names of the roles ("Administrator"). Which tenant they hold in is the
/// token's "tenant" claim (<see cref="Tenancy.TenantClaims"/>): a token is issued by one tenant, whose database
/// holds the roles. With the scope the access token is also issued for the application (its client_id is
/// among the audiences), so the application's API can accept it.
/// </summary>
public static class RoleClaims
{
    public const string Scope = "roles";

    public const string Claim = "roles";

    /// <summary>The roles the user has in the application, their own and their groups', sorted.</summary>
    public static async Task<List<string>> ForUserAsync(AnubisDbContext db, string userId, string clientId, CancellationToken cancellationToken = default)
    {
        var groupIds = db.UserRoles.Where(membership => membership.UserId == userId).Select(membership => membership.RoleId);
        var roles = await db.RoleAssignments
            .Where(a => a.Role!.Client!.ClientId == clientId && (a.UserId == userId || groupIds.Contains(a.GroupId!)))
            .Select(a => a.Role!.Name)
            .Distinct()
            .ToListAsync(cancellationToken);
        return roles.Order(StringComparer.Ordinal).ToList();
    }
}
