using System.Text.RegularExpressions;

using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Models;

using Microsoft.AspNetCore.Identity;

namespace k6labs.Anubis.Services;

/// <summary>
/// Editing a role in the admin UI: its name (not for the built-in Admin / User roles) and its claims,
/// validated first and saved in one transaction. Members' sessions are renewed, so tokens pick up the change.
/// </summary>
public sealed partial class RoleAdministration
{
    public const int NameMaxLength = 100;

    private readonly RoleManager<Role> _roles;
    private readonly UserManager<User> _users;
    private readonly AnubisDbContext _db;

    public RoleAdministration(RoleManager<Role> roles, UserManager<User> users, AnubisDbContext db)
    {
        _roles = roles;
        _users = users;
        _db = db;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9\-_\.]+$")]
    private static partial Regex NamePattern();

    public static bool IsBuiltIn(string? name)
        => string.Equals(name, DatabaseInitializer.AdminRole, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, DatabaseInitializer.UserRole, StringComparison.OrdinalIgnoreCase);

    /// <summary>Role names may contain letters, digits, '-', '_' and '.'.</summary>
    public static bool IsValidName(string name)
        => name.Length > 0 && NamePattern().IsMatch(name);

    public async Task<RoleEditModel?> LoadAsync(string roleId)
    {
        var role = await _roles.FindByIdAsync(roleId);
        if (role is null)
        {
            return null;
        }

        await _db.Entry(role).ReloadAsync();
        return new RoleEditModel
        {
            Id = role.Id,
            ConcurrencyStamp = role.ConcurrencyStamp,
            Name = role.Name ?? string.Empty,
            BuiltIn = IsBuiltIn(role.Name),
            Claims = CustomClaims.ToEntries(await _roles.GetClaimsAsync(role)),
        };
    }

    public async Task<Dictionary<string, string>> UpdateAsync(RoleEditModel model)
    {
        var errors = new Dictionary<string, string>();
        var role = await _roles.FindByIdAsync(model.Id);
        if (role is null)
        {
            errors[string.Empty] = Translations.Roles_NotFound;
            return errors;
        }

        await _db.Entry(role).ReloadAsync();
        if (model.ConcurrencyStamp is not null && model.ConcurrencyStamp != role.ConcurrencyStamp)
        {
            errors[string.Empty] = Translations.Identity_ConcurrencyFailure;
            return errors;
        }

        var name = model.Name.Trim();
        var renamed = !string.Equals(name, role.Name, StringComparison.Ordinal);
        if (renamed)
        {
            if (IsBuiltIn(role.Name))
            {
                errors[nameof(RoleEditModel.Name)] = Translations.Roles_BuiltInName;
            }
            else if (name.Length == 0)
            {
                errors[nameof(RoleEditModel.Name)] = Translations.Validation_RoleNameRequired;
            }
            else if (name.Length > NameMaxLength)
            {
                errors[nameof(RoleEditModel.Name)] = Translations.Validation_NameTooLong;
            }
            else if (!NamePattern().IsMatch(name))
            {
                errors[nameof(RoleEditModel.Name)] = Translations.Validation_Identifier;
            }
            else if (IsBuiltIn(name) || (await _roles.FindByNameAsync(name) is { } other && other.Id != role.Id))
            {
                errors[nameof(RoleEditModel.Name)] = Translations.Roles_NameInUse;
            }
        }

        var claims = CustomClaims.Validate(model.Claims, errors);
        if (errors.Count > 0)
        {
            return errors;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var current = (await _roles.GetClaimsAsync(role)).Where(claim => !CustomClaims.IsReserved(claim.Type)).ToList();
        var removed = current.Where(c => !claims.Any(n => n.Type == c.Type && n.Value == c.Value)).ToList();
        var added = claims.Where(n => !current.Any(c => c.Type == n.Type && c.Value == n.Value)).ToList();

        foreach (var claim in removed)
        {
            if (!Collect(errors, await _roles.RemoveClaimAsync(role, claim)))
            {
                return errors;
            }
        }

        foreach (var claim in added)
        {
            if (!Collect(errors, await _roles.AddClaimAsync(role, claim)))
            {
                return errors;
            }
        }

        if (renamed && !Collect(errors, await _roles.SetRoleNameAsync(role, name), nameof(RoleEditModel.Name)))
        {
            return errors;
        }

        if (!Collect(errors, await _roles.UpdateAsync(role)))
        {
            return errors;
        }

        if (renamed || removed.Count > 0 || added.Count > 0)
        {
            // Sign-in cookies and tokens carry roles and claims: renew the members' sessions.
            foreach (var member in await _users.GetUsersInRoleAsync(role.Name!))
            {
                await _users.UpdateSecurityStampAsync(member);
            }
        }

        await transaction.CommitAsync();
        return errors;
    }

    private static bool Collect(Dictionary<string, string> errors, IdentityResult result, string field = "")
    {
        if (result.Succeeded)
        {
            return true;
        }

        errors[field] = string.Join(" ", result.Errors.Select(e => e.Description));
        return false;
    }
}
