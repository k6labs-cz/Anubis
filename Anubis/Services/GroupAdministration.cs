using System.Text.RegularExpressions;

using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Models;

using Microsoft.AspNetCore.Identity;

using Group = k6labs.Anubis.Data.Entities.Group;

namespace k6labs.Anubis.Services;

/// <summary>
/// Editing a group in the admin UI: its name (not for the built-in Admins / Users groups) and its claims,
/// validated first and saved in one transaction. Members' sessions are renewed, so tokens pick up the change.
/// </summary>
public sealed partial class GroupAdministration
{
    public const int NameMaxLength = 100;

    private readonly RoleManager<Group> _groups;
    private readonly UserManager<User> _users;
    private readonly AnubisDbContext _db;

    public GroupAdministration(RoleManager<Group> groups, UserManager<User> users, AnubisDbContext db)
    {
        _groups = groups;
        _users = users;
        _db = db;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9\-_\.]+$")]
    private static partial Regex NamePattern();

    public static bool IsBuiltIn(string? name)
        => string.Equals(name, DatabaseInitializer.AdminGroup, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, DatabaseInitializer.UserGroup, StringComparison.OrdinalIgnoreCase);

    /// <summary>Group names may contain letters, digits, '-', '_' and '.'.</summary>
    public static bool IsValidName(string name)
        => name.Length > 0 && NamePattern().IsMatch(name);

    public async Task<GroupEditModel?> LoadAsync(string groupId)
    {
        var group = await _groups.FindByIdAsync(groupId);
        if (group is null)
        {
            return null;
        }

        await _db.Entry(group).ReloadAsync();
        return new GroupEditModel
        {
            Id = group.Id,
            ConcurrencyStamp = group.ConcurrencyStamp,
            Name = group.Name ?? string.Empty,
            BuiltIn = IsBuiltIn(group.Name),
            Claims = CustomClaims.ToEntries(await _groups.GetClaimsAsync(group)),
        };
    }

    public async Task<Dictionary<string, string>> UpdateAsync(GroupEditModel model)
    {
        var errors = new Dictionary<string, string>();
        var group = await _groups.FindByIdAsync(model.Id);
        if (group is null)
        {
            errors[string.Empty] = Translations.Groups_NotFound;
            return errors;
        }

        await _db.Entry(group).ReloadAsync();
        if (model.ConcurrencyStamp is not null && model.ConcurrencyStamp != group.ConcurrencyStamp)
        {
            errors[string.Empty] = Translations.Identity_ConcurrencyFailure;
            return errors;
        }

        var name = model.Name.Trim();
        var renamed = !string.Equals(name, group.Name, StringComparison.Ordinal);
        if (renamed)
        {
            if (IsBuiltIn(group.Name))
            {
                errors[nameof(GroupEditModel.Name)] = Translations.Groups_BuiltInName;
            }
            else if (name.Length == 0)
            {
                errors[nameof(GroupEditModel.Name)] = Translations.Validation_GroupNameRequired;
            }
            else if (name.Length > NameMaxLength)
            {
                errors[nameof(GroupEditModel.Name)] = Translations.Validation_NameTooLong;
            }
            else if (!NamePattern().IsMatch(name))
            {
                errors[nameof(GroupEditModel.Name)] = Translations.Validation_Identifier;
            }
            else if (IsBuiltIn(name) || (await _groups.FindByNameAsync(name) is { } other && other.Id != group.Id))
            {
                errors[nameof(GroupEditModel.Name)] = Translations.Groups_NameInUse;
            }
        }

        var claims = CustomClaims.Validate(model.Claims, errors);
        if (errors.Count > 0)
        {
            return errors;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var current = (await _groups.GetClaimsAsync(group)).Where(claim => !CustomClaims.IsReserved(claim.Type)).ToList();
        var removed = current.Where(c => !claims.Any(n => n.Type == c.Type && n.Value == c.Value)).ToList();
        var added = claims.Where(n => !current.Any(c => c.Type == n.Type && c.Value == n.Value)).ToList();

        foreach (var claim in removed)
        {
            if (!Collect(errors, await _groups.RemoveClaimAsync(group, claim)))
            {
                return errors;
            }
        }

        foreach (var claim in added)
        {
            if (!Collect(errors, await _groups.AddClaimAsync(group, claim)))
            {
                return errors;
            }
        }

        if (renamed && !Collect(errors, await _groups.SetRoleNameAsync(group, name), nameof(GroupEditModel.Name)))
        {
            return errors;
        }

        if (!Collect(errors, await _groups.UpdateAsync(group)))
        {
            return errors;
        }

        if (renamed || removed.Count > 0 || added.Count > 0)
        {
            // Sign-in cookies and tokens carry groups and claims: renew the members' sessions.
            foreach (var member in await _users.GetUsersInRoleAsync(group.Name!))
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
