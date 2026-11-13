using System.Text.RegularExpressions;

using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Resources;

using Microsoft.EntityFrameworkCore;

using OpenIddict.Abstractions;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace k6labs.Anubis.Services;

/// <summary>A role of an application; <c>ClientId</c> is the application's client_id.</summary>
public sealed record RoleSummary(string Id, string ClientId, string? ClientName, string Name, string? Description, int Assignments);

/// <summary>A role given to a user or a group (one of the two is set).</summary>
public sealed record AssignmentSummary(
    string Id, string RoleId, string ClientId, string RoleName,
    string? UserId, string? UserName, string? GroupId, string? GroupName, DateTimeOffset CreatedAt)
{
    /// <summary>The role as applications see it in the "roles" claim.</summary>
    public string Claim => RoleName;
}

public enum RoleError
{
    Invalid,
    Conflict,
    NotFound,
}

/// <summary>A change that can't be made; the message is for the administrator.</summary>
public sealed class RoleAdministrationException(RoleError error, string message) : Exception(message)
{
    public RoleError Error { get; } = error;
}

/// <summary>
/// The roles the applications of a tenant define, and who has them: the admin UI, the SCIM API and the
/// seeding at startup all change them here. Taking a role away revokes the access tokens it is written in,
/// so an application that asks Anubis about a token (introspection) sees the change at once.
/// </summary>
public sealed partial class RoleAdministration
{
    private readonly AnubisDbContext _db;
    private readonly IOpenIddictTokenManager _tokens;

    public RoleAdministration(AnubisDbContext db, IOpenIddictTokenManager tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    // ---------------------------------------------------------------- roles

    /// <summary>The roles of one application (by client_id), or of all.</summary>
    public async Task<List<RoleSummary>> ListRolesAsync(string? clientId = null)
        => await _db.ClientRoles
            .Where(r => clientId == null || r.Client!.ClientId == clientId)
            .OrderBy(r => r.Client!.ClientId).ThenBy(r => r.Name)
            .Select(r => new RoleSummary(r.Id, r.Client!.ClientId!, r.Client.DisplayName, r.Name, r.Description, _db.RoleAssignments.Count(a => a.RoleId == r.Id)))
            .ToListAsync();

    public async Task<RoleSummary?> FindRoleAsync(string id)
        => (await ListRolesAsync()).FirstOrDefault(r => r.Id == id);

    public async Task<RoleSummary> CreateRoleAsync(string? clientId, string? name, string? description)
    {
        name = (name ?? string.Empty).Trim();
        if (!RoleNamePattern().IsMatch(name))
        {
            throw new RoleAdministrationException(RoleError.Invalid, Translations.Error_RoleNameInvalid);
        }

        var client = await _db.Set<Client>().FirstOrDefaultAsync(c => c.ClientId == clientId)
            ?? throw new RoleAdministrationException(RoleError.Invalid, Translations.Error_ClientNotFound);
        if (await _db.ClientRoles.AnyAsync(r => r.ClientId == client.Id && r.Name == name))
        {
            throw new RoleAdministrationException(RoleError.Conflict, Translations.Error_RoleExists.F(name));
        }

        var role = new ClientRole { ClientId = client.Id!, Name = name, Description = Clean(description, 500) };
        _db.ClientRoles.Add(role);
        await _db.SaveChangesAsync();
        return new RoleSummary(role.Id, client.ClientId!, client.DisplayName, role.Name, role.Description, 0);
    }

    /// <summary>A role's name is what the application checks, so only its description changes.</summary>
    public async Task<RoleSummary> DescribeRoleAsync(string id, string? description)
    {
        var role = await _db.ClientRoles.FindAsync(id) ?? throw NotFound(id);
        role.Description = Clean(description, 500);
        await _db.SaveChangesAsync();
        return (await FindRoleAsync(id))!;
    }

    public async Task DeleteRoleAsync(string id)
    {
        var role = await _db.ClientRoles.FindAsync(id) ?? throw NotFound(id);
        var affected = await AffectedAsync(_db.RoleAssignments.Where(a => a.RoleId == id));
        _db.ClientRoles.Remove(role);
        await _db.SaveChangesAsync();
        await RevokeAsync(affected);
    }

    // ---------------------------------------------------------------- assignments

    public async Task<List<AssignmentSummary>> ListAssignmentsAsync()
        => await _db.RoleAssignments
            .OrderBy(a => a.Role!.Client!.ClientId).ThenBy(a => a.Role!.Name).ThenBy(a => a.Id)
            .Select(a => new AssignmentSummary(
                a.Id, a.RoleId, a.Role!.Client!.ClientId!, a.Role.Name,
                a.UserId, a.User!.Email, a.GroupId, a.Group!.Name, a.CreatedAt))
            .ToListAsync();

    public async Task<AssignmentSummary?> FindAssignmentAsync(string id)
        => (await ListAssignmentsAsync()).FirstOrDefault(a => a.Id == id);

    /// <summary>Gives the role to a user or a group (exactly one id).</summary>
    public async Task<AssignmentSummary> AssignAsync(string? roleId, string? userId, string? groupId)
    {
        userId = Empty(userId);
        groupId = Empty(groupId);
        if ((userId is null) == (groupId is null))
        {
            throw new RoleAdministrationException(RoleError.Invalid, Translations.Error_AssignmentPrincipal);
        }

        if (!await _db.ClientRoles.AnyAsync(r => r.Id == roleId))
        {
            throw NotFound(roleId);
        }

        if (userId is not null && !await _db.Users.AnyAsync(u => u.Id == userId))
        {
            throw NotFound(userId);
        }

        if (groupId is not null && !await _db.Roles.AnyAsync(g => g.Id == groupId))
        {
            throw NotFound(groupId);
        }

        if (await _db.RoleAssignments.AnyAsync(a => a.RoleId == roleId && a.UserId == userId && a.GroupId == groupId))
        {
            throw new RoleAdministrationException(RoleError.Conflict, Translations.Error_AssignmentExists);
        }

        var assignment = new RoleAssignment { RoleId = roleId!, UserId = userId, GroupId = groupId };
        _db.RoleAssignments.Add(assignment);
        await _db.SaveChangesAsync();
        return (await FindAssignmentAsync(assignment.Id))!;
    }

    public async Task RemoveAssignmentAsync(string id)
    {
        var assignment = await _db.RoleAssignments.FindAsync(id) ?? throw NotFound(id);
        var affected = await AffectedAsync(_db.RoleAssignments.Where(a => a.Id == id));
        _db.RoleAssignments.Remove(assignment);
        await _db.SaveChangesAsync();
        await RevokeAsync(affected);
    }

    // ---------------------------------------------------------------- choices for the admin UI

    /// <summary>The applications a role can be made for: client_id and display name.</summary>
    public async Task<List<(string ClientId, string? DisplayName)>> ListClientsAsync()
        => (await _db.Set<Client>().OrderBy(c => c.ClientId).Select(c => new { c.ClientId, c.DisplayName }).ToListAsync())
            .Select(c => (c.ClientId!, c.DisplayName)).ToList();

    /// <summary>Who a role can be given to: users (id, e-mail) and groups (id, name).</summary>
    public async Task<(List<(string Id, string Name)> Users, List<(string Id, string Name)> Groups)> ListPrincipalsAsync()
    {
        var users = await _db.Users.OrderBy(u => u.Email).Select(u => new { u.Id, u.Email }).ToListAsync();
        var groups = await _db.Roles.OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync();
        return (users.Select(u => (u.Id, u.Email ?? u.Id)).ToList(), groups.Select(g => (g.Id, g.Name ?? g.Id)).ToList());
    }

    // ---------------------------------------------------------------- revocation

    /// <summary>Who loses roles when the assignments go, and in which applications: users directly and through their groups.</summary>
    private async Task<List<(string UserId, string ClientEntityId)>> AffectedAsync(IQueryable<RoleAssignment> assignments)
    {
        var direct = await assignments.Where(a => a.UserId != null)
            .Select(a => new { UserId = a.UserId!, a.Role!.ClientId }).ToListAsync();
        var throughGroups = await assignments.Where(a => a.GroupId != null)
            .Join(_db.UserRoles, a => a.GroupId, membership => membership.RoleId, (a, membership) => new { membership.UserId, a.Role!.ClientId })
            .ToListAsync();
        return direct.Concat(throughGroups).Select(x => (x.UserId, x.ClientId)).Distinct().ToList();
    }

    /// <summary>Revokes the users' valid access tokens for the applications; refresh tokens stay and bring fresh roles.</summary>
    private async Task RevokeAsync(List<(string UserId, string ClientEntityId)> affected)
    {
        foreach (var (userId, clientEntityId) in affected)
        {
            var tokens = new List<object>();
            await foreach (var token in _tokens.FindAsync(userId, clientEntityId, Statuses.Valid, TokenTypeIdentifiers.AccessToken))
            {
                tokens.Add(token);
            }

            foreach (var token in tokens)
            {
                await _tokens.TryRevokeAsync(token);
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static RoleAdministrationException NotFound(string? id)
        => new(RoleError.NotFound, Translations.Error_RoleItemNotFound.F(id));

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Clean(string? value, int max)
        => Empty(value) is { } text ? (text.Length > max ? text[..max] : text) : null;

    /// <summary>No whitespace, and no colon: applications that knew roles as "tenant:Role" split the claim at it.</summary>
    [GeneratedRegex(@"^[^\s:]{1,100}$")]
    private static partial Regex RoleNamePattern();
}
