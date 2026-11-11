using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using k6labs.Anubis.Data;
using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Models;
using k6labs.Anubis.Scim;
using k6labs.Anubis.Services.Sms;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Group = k6labs.Anubis.Data.Entities.Group;

namespace k6labs.Anubis.Services;

/// <summary>
/// The SCIM 2.0 provisioning API (RFC 7643 / 7644) under /api/: users and groups with their
/// custom claims. All changes go through the same <see cref="UserManager{User}"/> / <see cref="RoleManager{Group}"/>
/// as the admin UI (validation, account history, security stamps), inside one database transaction.
/// Failures are thrown as <see cref="ScimException"/> and rendered as SCIM errors by the controllers.
/// </summary>
public sealed partial class ScimService
{
    private readonly UserManager<User> _users;
    private readonly RoleManager<Group> _groups;
    private readonly AnubisDbContext _db;

    public ScimService(UserManager<User> users, RoleManager<Group> groups, AnubisDbContext db)
    {
        _users = users;
        _groups = groups;
        _db = db;
    }

    // ---------------------------------------------------------------------------
    // Users: list / get
    // ---------------------------------------------------------------------------

    public async Task<JsonObject> ListUsersAsync(string? filter, string? sortBy, string? sortOrder, int startIndex, int count, string? attributes, string? excludedAttributes, string baseUri)
    {
        var groupIds = await GroupIdMapAsync();
        var resources = new List<JsonObject>();
        foreach (var user in await _users.Users.ToListAsync())
        {
            resources.Add(await ToUserJsonAsync(user, groupIds, baseUri));
        }

        var terms = ScimFilter.Parse(filter);
        var filtered = resources.Where(r => ScimFilter.Matches(r, terms)).ToList();
        var sorted = SortResources(filtered, sortBy, sortOrder, defaultKey: "username", allowed: ["id", "username", "displayname", "meta.created"]);

        var page = sorted.Skip(startIndex - 1).Take(count).ToList();
        foreach (var resource in page)
        {
            ScimResources.ApplySelection(resource, attributes, excludedAttributes);
        }

        return ScimResources.ListResponse(filtered.Count, startIndex, page);
    }

    public async Task<JsonObject> GetUserAsync(string id, string? attributes, string? excludedAttributes, string baseUri)
    {
        var user = await FindUserAsync(id);
        var resource = await ToUserJsonAsync(user, await GroupIdMapAsync(), baseUri);
        ScimResources.ApplySelection(resource, attributes, excludedAttributes);
        return resource;
    }

    // ---------------------------------------------------------------------------
    // Users: create / replace / patch / delete
    // ---------------------------------------------------------------------------

    public async Task<JsonObject> CreateUserAsync(ScimUserResource resource, string baseUri)
    {
        var email = RequiredEmail(resource);
        await ValidateEmailAsync(email, userId: null);

        var displayName = DisplayNameOf(resource, email);
        var phone = PhoneOf(resource);
        var claims = ValidateClaims(resource.Extension?.Claims);
        var groups = await ResolveGroupsAsync(resource.Groups);
        groups.Add(DatabaseInitializer.UserGroup);

        var user = new User
        {
            UserName = email,
            Email = email,
            EmailConfirmed = resource.Extension?.EmailConfirmed ?? true,   // provisioned by a trusted client
            DisplayName = displayName,
            PhoneNumber = phone,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var created = string.IsNullOrEmpty(resource.Password)
            ? await _users.CreateAsync(user)
            : await _users.CreateAsync(user, resource.Password);
        if (!created.Succeeded)
        {
            throw ScimException.InvalidValue(string.Join(" ", created.Errors.Select(e => e.Description)));
        }

        var currentGroups = await _users.GetRolesAsync(user);   // the very first account already got Admin
        var missing = groups.Where(group => !currentGroups.Contains(group, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0 && !(await _users.AddToRolesAsync(user, missing)).Succeeded)
        {
            throw ScimException.InvalidValue($"Could not assign the groups: {string.Join(", ", missing)}.");
        }

        if (claims.Count > 0 && !(await _users.AddClaimsAsync(user, claims)).Succeeded)
        {
            throw ScimException.InvalidValue("Could not save the claims.");
        }

        if (resource.Active == false)
        {
            await _users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddYears(100));
        }

        await transaction.CommitAsync();
        return await ToUserJsonAsync(user, await GroupIdMapAsync(), baseUri);
    }

    /// <summary>PUT: full replace. Attributes that are absent are reset to their defaults (RFC 7644 section 3.5.1);
    /// the password is only changed when one is sent.</summary>
    public async Task<JsonObject> ReplaceUserAsync(string id, ScimUserResource resource, string baseUri)
    {
        var user = await FindUserAsync(id);
        await _db.Entry(user).ReloadAsync();

        var email = RequiredEmail(resource);
        await ValidateEmailAsync(email, user.Id);

        var displayName = DisplayNameOf(resource, email);
        var phone = PhoneOf(resource);
        var claims = ValidateClaims(resource.Extension?.Claims);
        var groups = resource.Groups is null
            ? new List<string> { DatabaseInitializer.UserGroup }
            : (await ResolveGroupsAsync(resource.Groups)).Append(DatabaseInitializer.UserGroup).ToList();
        await EnsureNotLastAdminAsync(user, await _users.GetRolesAsync(user), groups);

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var changed = false;

        if (!string.Equals(email, user.Email, StringComparison.Ordinal))
        {
            if (!(await _users.SetEmailAsync(user, email)).Succeeded || !(await _users.SetUserNameAsync(user, email)).Succeeded)
            {
                throw ScimException.InvalidValue($"The e-mail \"{email}\" could not be set.");
            }

            changed = true;
        }

        if (phone != user.PhoneNumber)
        {
            if (!(await _users.SetPhoneNumberAsync(user, phone)).Succeeded)
            {
                throw ScimException.InvalidValue("The phone number could not be set.");
            }

            changed = true;
        }

        user.DisplayName = displayName;
        user.EmailConfirmed = resource.Extension?.EmailConfirmed ?? user.EmailConfirmed;
        if (!(await _users.UpdateAsync(user)).Succeeded)
        {
            throw ScimException.InvalidValue("The account could not be saved.");
        }

        var currentGroups = await _users.GetRolesAsync(user);
        var toRemove = currentGroups.Where(group => !groups.Contains(group, StringComparer.OrdinalIgnoreCase)).ToList();
        var toAdd = groups.Where(group => !currentGroups.Contains(group, StringComparer.OrdinalIgnoreCase)).ToList();
        if (toRemove.Count > 0 && !(await _users.RemoveFromRolesAsync(user, toRemove)).Succeeded)
        {
            throw ScimException.InvalidValue($"Could not remove the groups: {string.Join(", ", toRemove)}.");
        }

        if (toAdd.Count > 0 && !(await _users.AddToRolesAsync(user, toAdd)).Succeeded)
        {
            throw ScimException.InvalidValue($"Could not assign the groups: {string.Join(", ", toAdd)}.");
        }

        changed |= toRemove.Count > 0 || toAdd.Count > 0;

        var currentClaims = (await _users.GetClaimsAsync(user)).Where(c => !CustomClaims.IsReserved(c.Type)).ToList();
        var removedClaims = currentClaims.Where(c => !claims.Any(n => n.Type == c.Type && n.Value == c.Value)).ToList();
        var addedClaims = claims.Where(n => !currentClaims.Any(c => c.Type == n.Type && c.Value == n.Value)).ToList();
        if (removedClaims.Count > 0 && !(await _users.RemoveClaimsAsync(user, removedClaims)).Succeeded
            || addedClaims.Count > 0 && !(await _users.AddClaimsAsync(user, addedClaims)).Succeeded)
        {
            throw ScimException.InvalidValue("The claims could not be saved.");
        }

        changed |= removedClaims.Count > 0 || addedClaims.Count > 0;

        if (!string.IsNullOrEmpty(resource.Password))
        {
            await SetPasswordAsync(user, resource.Password);
            changed = true;
        }

        var active = resource.Active ?? true;
        var locked = user.LockoutEnd is { } end && end > DateTimeOffset.UtcNow;
        if (active == locked)
        {
            await _users.SetLockoutEndDateAsync(user, active ? null : DateTimeOffset.UtcNow.AddYears(100));
            changed = true;
        }

        if (changed)
        {
            await _users.UpdateSecurityStampAsync(user);
        }

        await transaction.CommitAsync();
        return await ToUserJsonAsync(user, await GroupIdMapAsync(), baseUri);
    }

    /// <summary>PATCH: applies the operations of RFC 7644 section 3.5.2 one by one, in one transaction.</summary>
    public async Task<JsonObject> PatchUserAsync(string id, ScimPatchRequest patch, string baseUri)
    {
        if (patch.Operations is null || patch.Operations.Count == 0)
        {
            throw ScimException.InvalidSyntax("The PATCH request must contain at least one operation.");
        }

        if (patch.Schemas is not null && !patch.Schemas.Contains(ScimConstants.PatchRequestSchema, StringComparer.OrdinalIgnoreCase))
        {
            throw ScimException.InvalidSyntax($"The schemas must contain \"{ScimConstants.PatchRequestSchema}\".");
        }

        var user = await FindUserAsync(id);
        await _db.Entry(user).ReloadAsync();

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var changed = false;

        foreach (var operation in patch.Operations)
        {
            var op = operation.Op?.ToLowerInvariant();
            var path = operation.Path?.Trim();
            switch (op)
            {
                case "add" or "replace" when path is not null:
                    changed |= await ApplyUserValueAsync(user, op, path, operation.Value);
                    break;
                case "remove" when path is not null:
                    changed |= await RemoveUserValueAsync(user, path, operation.Value);
                    break;
                default:
                    throw ScimException.InvalidSyntax($"The operation \"{operation.Op}\" on path \"{operation.Path}\" is not supported.");
            }
        }

        if (changed)
        {
            await _users.UpdateSecurityStampAsync(user);
        }

        await transaction.CommitAsync();
        return await ToUserJsonAsync(user, await GroupIdMapAsync(), baseUri);
    }

    public async Task DeleteUserAsync(string id)
    {
        var user = await FindUserAsync(id);
        var result = await _users.DeleteAsync(user);
        if (!result.Succeeded)
        {
            throw ScimException.InvalidValue(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }

    // ---------------------------------------------------------------------------
    // Groups (groups): list / get
    // ---------------------------------------------------------------------------

    public async Task<JsonObject> ListGroupsAsync(string? filter, string? sortBy, string? sortOrder, int startIndex, int count, string? attributes, string? excludedAttributes, string baseUri)
    {
        var resources = new List<JsonObject>();
        foreach (var group in await _groups.Roles.ToListAsync())
        {
            resources.Add(await ToGroupJsonAsync(group, baseUri));
        }

        var terms = ScimFilter.Parse(filter);
        var filtered = resources.Where(r => ScimFilter.Matches(r, terms)).ToList();
        var sorted = SortResources(filtered, sortBy, sortOrder, defaultKey: "displayname", allowed: ["id", "displayname"]);

        var page = sorted.Skip(startIndex - 1).Take(count).ToList();
        foreach (var resource in page)
        {
            ScimResources.ApplySelection(resource, attributes, excludedAttributes);
        }

        return ScimResources.ListResponse(filtered.Count, startIndex, page);
    }

    public async Task<JsonObject> GetGroupAsync(string id, string? attributes, string? excludedAttributes, string baseUri)
    {
        var group = await FindGroupAsync(id);
        var resource = await ToGroupJsonAsync(group, baseUri);
        ScimResources.ApplySelection(resource, attributes, excludedAttributes);
        return resource;
    }

    // ---------------------------------------------------------------------------
    // Groups (groups): create / replace / patch / delete
    // ---------------------------------------------------------------------------

    public async Task<JsonObject> CreateGroupAsync(ScimGroupResource resource, string baseUri)
    {
        var name = await ValidateGroupNameAsync(resource.DisplayName, groupId: null);
        var members = await ResolveMembersAsync(resource.Members);

        var group = new Group(name);
        await using var transaction = await _db.Database.BeginTransactionAsync();
        if (!(await _groups.CreateAsync(group)).Succeeded)
        {
            throw ScimException.InvalidValue($"The group \"{name}\" could not be created.");
        }

        foreach (var member in members)
        {
            if (!(await _users.AddToRoleAsync(member, name)).Succeeded)
            {
                throw ScimException.InvalidValue($"Could not add {member.Email} to the group \"{name}\".");
            }
        }

        await transaction.CommitAsync();
        return await ToGroupJsonAsync(group, baseUri);
    }

    public async Task<JsonObject> ReplaceGroupAsync(string id, ScimGroupResource resource, string baseUri)
    {
        var group = await FindGroupAsync(id);
        await _db.Entry(group).ReloadAsync();

        var name = await ValidateGroupNameAsync(resource.DisplayName, group.Id, currentName: group.Name);
        var members = resource.Members is null ? new List<User>() : await ResolveMembersAsync(resource.Members);

        await using var transaction = await _db.Database.BeginTransactionAsync();
        if (!string.Equals(name, group.Name, StringComparison.Ordinal) && !(await _groups.SetRoleNameAsync(group, name)).Succeeded)
        {
            throw ScimException.InvalidValue($"The group could not be renamed to \"{name}\".");
        }

        if (!(await _groups.UpdateAsync(group)).Succeeded)
        {
            throw ScimException.InvalidValue("The group could not be saved.");
        }

        var currentMembers = await _users.GetUsersInRoleAsync(group.Name!);
        var toRemove = currentMembers.Where(u => !members.Any(m => m.Id == u.Id)).ToList();
        var toAdd = members.Where(m => !currentMembers.Any(u => u.Id == m.Id)).ToList();
        foreach (var member in toRemove)
        {
            await _users.RemoveFromRoleAsync(member, group.Name!);
        }

        foreach (var member in toAdd)
        {
            await _users.AddToRoleAsync(member, name);
        }

        if (toRemove.Count > 0 || toAdd.Count > 0 || !string.Equals(name, group.Name, StringComparison.Ordinal))
        {
            foreach (var member in await _users.GetUsersInRoleAsync(name))
            {
                await _users.UpdateSecurityStampAsync(member);
            }
        }

        await transaction.CommitAsync();
        return await ToGroupJsonAsync(group, baseUri);
    }

    public async Task<JsonObject> PatchGroupAsync(string id, ScimPatchRequest patch, string baseUri)
    {
        if (patch.Operations is null || patch.Operations.Count == 0)
        {
            throw ScimException.InvalidSyntax("The PATCH request must contain at least one operation.");
        }

        if (patch.Schemas is not null && !patch.Schemas.Contains(ScimConstants.PatchRequestSchema, StringComparer.OrdinalIgnoreCase))
        {
            throw ScimException.InvalidSyntax($"The schemas must contain \"{ScimConstants.PatchRequestSchema}\".");
        }

        var group = await FindGroupAsync(id);
        await _db.Entry(group).ReloadAsync();

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var changed = false;

        foreach (var operation in patch.Operations)
        {
            var op = operation.Op?.ToLowerInvariant();
            var path = operation.Path?.Trim();
            switch (op)
            {
                case "add" or "replace" when path is not null:
                    changed |= await ApplyGroupValueAsync(group, op, path, operation.Value);
                    break;
                case "remove" when path is not null:
                    changed |= await RemoveGroupValueAsync(group, path);
                    break;
                default:
                    throw ScimException.InvalidSyntax($"The operation \"{operation.Op}\" on path \"{operation.Path}\" is not supported.");
            }
        }

        if (changed)
        {
            foreach (var member in await _users.GetUsersInRoleAsync(group.Name!))
            {
                await _users.UpdateSecurityStampAsync(member);
            }
        }

        await transaction.CommitAsync();
        return await ToGroupJsonAsync(group, baseUri);
    }

    public async Task DeleteGroupAsync(string id)
    {
        var group = await FindGroupAsync(id);
        if (GroupAdministration.IsBuiltIn(group.Name))
        {
            throw new ScimException(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.Mutability,
                $"The built-in group \"{group.Name}\" cannot be deleted.");
        }

        var result = await _groups.DeleteAsync(group);
        if (!result.Succeeded)
        {
            throw ScimException.InvalidValue(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }

    // ---------------------------------------------------------------------------
    // PATCH helpers: users
    // ---------------------------------------------------------------------------

    private async Task<bool> ApplyUserValueAsync(User user, string op, string path, JsonNode? value)
    {
        var normalized = path.ToLowerInvariant();
        if (normalized.StartsWith(ScimConstants.UserExtensionSchema.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return await ApplyExtensionValueAsync(user, op, normalized[ScimConstants.UserExtensionSchema.Length..], value);
        }

        switch (normalized)
        {
            case "username":
                return await SetEmailAsync(user, RequiredString(value, "userName"));
            case "displayname":
                user.DisplayName = DisplayNameOf(value is null ? null : StringValue(value), user.Email ?? user.UserName ?? string.Empty);
                return await _users.UpdateAsync(user) is { Succeeded: true };
            case "name" or "name.formatted":
                user.DisplayName = DisplayNameOf(normalized == "name" ? NameFormatted(value) : StringValue(value), user.Email ?? user.UserName ?? string.Empty);
                return await _users.UpdateAsync(user) is { Succeeded: true };
            case "active":
                var active = BoolValue(value) ?? throw ScimException.InvalidValue("\"active\" must be a boolean.");
                await _users.SetLockoutEndDateAsync(user, active ? null : DateTimeOffset.UtcNow.AddYears(100));
                return true;
            case "password":
                await SetPasswordAsync(user, RequiredString(value, "password"));
                return true;
            case var p when p == "emails" || p.StartsWith("emails[", StringComparison.Ordinal) || p.StartsWith("emails.", StringComparison.Ordinal):
                return await SetEmailAsync(user, RequiredString(EmailValue(value), "emails"));
            case var p when p == "phonenumbers" || p.StartsWith("phonenumbers[", StringComparison.Ordinal) || p.StartsWith("phonenumbers.", StringComparison.Ordinal):
                var phone = PhoneValue(value);
                if (!(await _users.SetPhoneNumberAsync(user, phone)).Succeeded)
                {
                    throw ScimException.InvalidValue("The phone number could not be set.");
                }

                return true;
            case "groups":
                var refs = GroupRefsValue(value);
                var groups = await ResolveGroupsAsync(refs);
                var current = await _users.GetRolesAsync(user);
                var missing = groups.Where(r => !current.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
                await EnsureNotLastAdminAsync(user, current, current.Concat(missing).ToList());
                if (missing.Count > 0 && !(await _users.AddToRolesAsync(user, missing)).Succeeded)
                {
                    throw ScimException.InvalidValue($"Could not assign the groups: {string.Join(", ", missing)}.");
                }

                return missing.Count > 0;
            default:
                throw ScimException.InvalidPath(path);
        }
    }

    private async Task<bool> RemoveUserValueAsync(User user, string path, JsonNode? value)
    {
        var normalized = path.ToLowerInvariant();
        if (normalized.StartsWith(ScimConstants.UserExtensionSchema.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return await RemoveExtensionValueAsync(user, normalized[ScimConstants.UserExtensionSchema.Length..]);
        }

        switch (normalized)
        {
            case "username":
                throw new ScimException(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.Mutability, "userName is required and cannot be removed.");
            case "displayname" or "name" or "name.formatted":
                user.DisplayName = user.Email ?? user.UserName ?? string.Empty;
                return await _users.UpdateAsync(user) is { Succeeded: true };
            case "active":
                await _users.SetLockoutEndDateAsync(user, null);
                return true;
            case "password":
                await _users.RemovePasswordAsync(user);
                return true;
            case var p when p.StartsWith("phonenumbers", StringComparison.Ordinal):
                await _users.SetPhoneNumberAsync(user, null);
                return true;
            case "groups":
                return await RemoveGroupsAsync(user, filter: null);
            case var p when GroupFilter(p) is { } filter:
                return await RemoveGroupsAsync(user, filter);
            default:
                throw ScimException.InvalidPath(path);
        }
    }

    private async Task<bool> ApplyExtensionValueAsync(User user, string op, string rest, JsonNode? value)
    {
        // rest is "", ":claims", ":claims[type eq \"x\"]" or ":emailconfirmed" (lowercased).
        if (rest is ":emailconfirmed")
        {
            user.EmailConfirmed = op == "remove" ? false : BoolValue(value) ?? throw ScimException.InvalidValue("\"emailConfirmed\" must be a boolean.");
            return await _users.UpdateAsync(user) is { Succeeded: true };
        }

        if (rest is "")
        {
            // Whole extension replaced.
            if (value is not JsonObject extension)
            {
                throw ScimException.InvalidValue("The extension value must be an object.");
            }

            if (extension.TryGetPropertyValue("emailConfirmed", out var confirmedNode))
            {
                user.EmailConfirmed = BoolValue(confirmedNode) ?? user.EmailConfirmed;
            }

            var claims = ValidateClaims(ClaimsOf(extension.TryGetPropertyValue("claims", out var claimsNode) ? claimsNode : null));
            return await ReplaceClaimsAsync(user, claims);
        }

        if (rest is ":claims")
        {
            var claims = ValidateClaims(ClaimsOf(value));
            return op == "add"
                ? await AddClaimsAsync(user, claims)
                : await ReplaceClaimsAsync(user, claims);
        }

        var match = ClaimTypeFilterRegex().Match(rest);
        if (match.Success)
        {
            var type = match.Groups["type"].Value;
            var claims = ValidateClaims(ClaimsOf(value));
            return op == "add"
                ? await AddClaimsAsync(user, claims.Where(c => c.Type.Equals(type, StringComparison.Ordinal)).ToList())
                : await ReplaceClaimsOfTypeAsync(user, type, claims);
        }

        throw ScimException.InvalidPath(ScimConstants.UserExtensionSchema + rest);
    }

    private async Task<bool> RemoveExtensionValueAsync(User user, string rest)
    {
        if (rest is ":claims")
        {
            return await ReplaceClaimsAsync(user, new List<Claim>());
        }

        var match = ClaimTypeFilterRegex().Match(rest);
        if (match.Success)
        {
            return await ReplaceClaimsOfTypeAsync(user, match.Groups["type"].Value, new List<Claim>());
        }

        throw ScimException.InvalidPath(ScimConstants.UserExtensionSchema + rest);
    }

    private async Task<bool> SetEmailAsync(User user, string email)
    {
        email = email.Trim();
        await ValidateEmailAsync(email, user.Id);
        if (string.Equals(email, user.Email, StringComparison.Ordinal))
        {
            return false;
        }

        if (!(await _users.SetEmailAsync(user, email)).Succeeded || !(await _users.SetUserNameAsync(user, email)).Succeeded)
        {
            throw ScimException.InvalidValue($"The e-mail \"{email}\" could not be set.");
        }

        return true;
    }

    private async Task SetPasswordAsync(User user, string password)
    {
        // No current password is needed (provisioning), so a reset token is generated and used at once.
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var result = await _users.ResetPasswordAsync(user, token, password);
        if (!result.Succeeded)
        {
            throw ScimException.InvalidValue(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }

    private async Task<bool> ReplaceClaimsAsync(User user, List<Claim> claims)
    {
        var current = (await _users.GetClaimsAsync(user)).Where(c => !CustomClaims.IsReserved(c.Type)).ToList();
        var removed = current.Where(c => !claims.Any(n => n.Type == c.Type && n.Value == c.Value)).ToList();
        var added = claims.Where(n => !current.Any(c => c.Type == n.Type && c.Value == n.Value)).ToList();
        if (removed.Count > 0 && !(await _users.RemoveClaimsAsync(user, removed)).Succeeded
            || added.Count > 0 && !(await _users.AddClaimsAsync(user, added)).Succeeded)
        {
            throw ScimException.InvalidValue("The claims could not be saved.");
        }

        return removed.Count > 0 || added.Count > 0;
    }

    private async Task<bool> AddClaimsAsync(User user, List<Claim> claims)
    {
        if (claims.Count == 0)
        {
            return false;
        }

        var current = (await _users.GetClaimsAsync(user)).Where(c => !CustomClaims.IsReserved(c.Type)).ToList();
        var toAdd = claims.Where(c => !current.Any(n => n.Type == c.Type && n.Value == c.Value)).ToList();
        if (toAdd.Count > 0 && !(await _users.AddClaimsAsync(user, toAdd)).Succeeded)
        {
            throw ScimException.InvalidValue("The claims could not be saved.");
        }

        return toAdd.Count > 0;
    }

    private async Task<bool> ReplaceClaimsOfTypeAsync(User user, string type, List<Claim> claims)
    {
        var current = (await _users.GetClaimsAsync(user)).Where(c => !CustomClaims.IsReserved(c.Type)).ToList();
        var kept = current.Where(c => !c.Type.Equals(type, StringComparison.Ordinal)).ToList();
        return await ReplaceClaimsAsync(user, kept.Concat(claims).ToList());
    }

    private async Task<bool> RemoveGroupsAsync(User user, (string Attr, string Value)? filter)
    {
        var current = await _users.GetRolesAsync(user);
        List<string> toRemove;
        if (filter is { } f)
        {
            var group = f.Attr == "display"
                ? await _groups.FindByNameAsync(f.Value)
                : await _groups.FindByIdAsync(f.Value);
            if (group?.Name is null)
            {
                throw new ScimException(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.NoTarget, $"No group matches \"{f.Attr} eq \\\"{f.Value}\\\"\".");
            }

            toRemove = current.Where(r => r.Equals(group.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            toRemove = current.Where(r => !GroupAdministration.IsBuiltIn(r)).ToList();
        }

        await EnsureNotLastAdminAsync(user, current, current.Where(r => !toRemove.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList());
        if (toRemove.Count > 0 && !(await _users.RemoveFromRolesAsync(user, toRemove)).Succeeded)
        {
            throw ScimException.InvalidValue($"Could not remove the groups: {string.Join(", ", toRemove)}.");
        }

        return toRemove.Count > 0;
    }

    // ---------------------------------------------------------------------------
    // PATCH helpers: groups
    // ---------------------------------------------------------------------------

    private async Task<bool> ApplyGroupValueAsync(Group group, string op, string path, JsonNode? value)
    {
        switch (path.ToLowerInvariant())
        {
            case "displayname":
                var name = await ValidateGroupNameAsync(StringValue(value), group.Id, currentName: group.Name);
                if (!string.Equals(name, group.Name, StringComparison.Ordinal))
                {
                    if (!(await _groups.SetRoleNameAsync(group, name)).Succeeded || !(await _groups.UpdateAsync(group)).Succeeded)
                    {
                        throw ScimException.InvalidValue($"The group could not be renamed to \"{name}\".");
                    }

                    return true;
                }

                return false;
            case "members":
                var members = await ResolveMembersAsync(MembersOf(value));
                foreach (var member in members)
                {
                    if (!(await _users.AddToRoleAsync(member, group.Name!)).Succeeded)
                    {
                        throw ScimException.InvalidValue($"Could not add {member.Email} to the group \"{group.Name}\".");
                    }
                }

                return members.Count > 0;
            default:
                throw ScimException.InvalidPath(path);
        }
    }

    private async Task<bool> RemoveGroupValueAsync(Group group, string path)
    {
        if (path.Equals("members", StringComparison.OrdinalIgnoreCase))
        {
            var removed = false;
            foreach (var member in await _users.GetUsersInRoleAsync(group.Name!))
            {
                if ((await _users.RemoveFromRoleAsync(member, group.Name!)).Succeeded)
                {
                    removed = true;
                }
            }

            return removed;
        }

        var match = MemberFilterRegex().Match(path);
        if (match.Success)
        {
            var user = await FindUserAsync(match.Groups["id"].Value);
            if (!(await _users.RemoveFromRoleAsync(user, group.Name!)).Succeeded)
            {
                throw ScimException.InvalidValue($"Could not remove {user.Email} from the group \"{group.Name}\".");
            }

            return true;
        }

        throw ScimException.InvalidPath(path);
    }

    // ---------------------------------------------------------------------------
    // Mapping and shared validation
    // ---------------------------------------------------------------------------

    private async Task<JsonObject> ToUserJsonAsync(User user, IReadOnlyDictionary<string, string> groupIds, string baseUri)
    {
        var groups = await _users.GetRolesAsync(user);
        var claims = (await _users.GetClaimsAsync(user)).Where(c => !CustomClaims.IsReserved(c.Type)).ToList();
        return ScimResources.UserResource(user, groups, groupIds, claims, baseUri);
    }

    private async Task<JsonObject> ToGroupJsonAsync(Group group, string baseUri)
    {
        var members = (await _users.GetUsersInRoleAsync(group.Name!))
            .Select(u => (u.Id, (string?)u.Email ?? u.UserName))
            .ToList();
        return ScimResources.GroupResource(group, members, baseUri);
    }

    private async Task<Dictionary<string, string>> GroupIdMapAsync()
        => await _groups.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id, StringComparer.OrdinalIgnoreCase);

    private async Task<User> FindUserAsync(string id)
        => await _users.FindByIdAsync(id) ?? throw ScimException.NotFound("User", id);

    private async Task<Group> FindGroupAsync(string id)
        => await _groups.FindByIdAsync(id) ?? throw ScimException.NotFound("Group", id);

    internal static List<JsonObject> SortResources(List<JsonObject> resources, string? sortBy, string? sortOrder, string defaultKey, string[] allowed)
    {
        var key = string.IsNullOrWhiteSpace(sortBy) ? defaultKey : sortBy.ToLowerInvariant();
        if (!allowed.Contains(key))
        {
            throw new ScimException(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.InvalidFilter, $"Sorting by \"{sortBy}\" is not supported.");
        }

        var sorted = resources.OrderBy(r => ScimFilter.AttributeValue(r, key) ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
        if (string.Equals(sortOrder, "descending", StringComparison.OrdinalIgnoreCase) || string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase))
        {
            sorted.Reverse();
        }

        return sorted;
    }

    private static string RequiredEmail(ScimUserResource resource)
    {
        var email = resource.UserName?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            email = resource.Emails?.FirstOrDefault(e => e.Primary == true)?.Value
                ?? resource.Emails?.FirstOrDefault()?.Value;
        }

        return email ?? string.Empty;
    }

    private static string DisplayNameOf(ScimUserResource resource, string email)
        => DisplayNameOf(resource.DisplayName ?? resource.Name?.Formatted ?? NameFormatted(null), email);

    private static string DisplayNameOf(string? displayName, string email)
    {
        displayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName.Trim();
        if (displayName.Length > UserAdministration.DisplayNameMaxLength)
        {
            throw ScimException.InvalidValue($"The display name is longer than {UserAdministration.DisplayNameMaxLength} characters.");
        }

        return displayName;
    }

    private static string? NameFormatted(JsonNode? name)
        => name is JsonObject obj
            && obj.TryGetPropertyValue("formatted", out var formatted)
            ? StringValue(formatted)
            : null;

    private static string? PhoneOf(ScimUserResource resource)
        => PhoneValue(resource.PhoneNumbers is { Count: > 0 } phones
            ? new JsonArray(phones.Select(p => (JsonNode?)JsonValueOf(p.Value)).ToArray())
            : null);

    private static string? PhoneValue(JsonNode? value)
    {
        var raw = EmailValue(value);   // same normalization: array -> primary/first, object -> .value, string -> itself
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var phone = PhoneNumberService.Normalize(raw);
        return phone ?? throw ScimException.InvalidValue($"\"{raw}\" is not a valid phone number.");
    }

    /// <summary>Normalizes an emails/phoneNumbers PATCH value: array -> primary or first entry, object -> its "value", string -> itself.</summary>
    private static string? EmailValue(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonValue jsonValue:
                return jsonValue.TryGetValue<string>(out var text) ? text : null;
            case JsonObject obj:
                return obj.TryGetPropertyValue("value", out var v) ? StringValue(v) : null;
            case JsonArray array:
                var primary = array.OfType<JsonObject>().FirstOrDefault(e => e.TryGetPropertyValue("primary", out var p) && p is JsonValue pv && pv.TryGetValue<bool>(out var b) && b)
                    ?? array.FirstOrDefault();
                return primary is JsonObject entry && entry.TryGetPropertyValue("value", out var entryValue) ? StringValue(entryValue) : StringValue(primary);
            default:
                return null;
        }
    }

    private static List<ScimGroupRef> GroupRefsValue(JsonNode? value)
        => value switch
        {
            JsonArray array => array.Select(GroupRefOf).Where(r => r is not null).Cast<ScimGroupRef>().ToList(),
            _ => GroupRefOf(value) is { } single ? new List<ScimGroupRef> { single } : new List<ScimGroupRef>(),
        };

    private static ScimGroupRef? GroupRefOf(JsonNode? node)
        => node is JsonObject obj
            ? new ScimGroupRef { Value = StringValue(obj.TryGetPropertyValue("value", out var v) ? v : null), Display = StringValue(obj.TryGetPropertyValue("display", out var d) ? d : null) }
            : null;

    private static List<ScimMember> MembersOf(JsonNode? value)
        => value switch
        {
            JsonArray array => array.OfType<JsonObject>().Select(MemberOf).Where(m => m is not null).Cast<ScimMember>().ToList(),
            JsonObject obj => MemberOf(obj) is { } single ? new List<ScimMember> { single } : new List<ScimMember>(),
            _ => new List<ScimMember>(),
        };

    private static ScimMember? MemberOf(JsonObject obj)
        => obj.TryGetPropertyValue("value", out var v) && StringValue(v) is { Length: > 0 } id
            ? new ScimMember { Value = id, Display = StringValue(obj.TryGetPropertyValue("display", out var d) ? d : null) }
            : null;

    private static List<ScimClaim>? ClaimsOf(JsonNode? value)
        => value switch
        {
            JsonArray array => array.OfType<JsonObject>().Select(ClaimOf).Where(c => c is not null).Cast<ScimClaim>().ToList(),
            JsonObject obj => ClaimOf(obj) is { } single ? new List<ScimClaim> { single } : new List<ScimClaim>(),
            _ => null,
        };

    private static ScimClaim? ClaimOf(JsonObject obj)
        => new() { Type = StringValue(obj.TryGetPropertyValue("type", out var t) ? t : null), Value = StringValue(obj.TryGetPropertyValue("value", out var v) ? v : null) };

    private static string? StringValue(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? BoolValue(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : null;

    private static string RequiredString(JsonNode? node, string attribute)
        => StringValue(node) is { Length: > 0 } text
            ? text
            : throw ScimException.InvalidValue($"\"{attribute}\" must be a non-empty string.");

    private static JsonValue? JsonValueOf(string? value)
        => value is null ? null : JsonValue.Create(value);

    private static (string Attr, string Value)? GroupFilter(string path)
    {
        var match = GroupFilterRegex().Match(path.ToLowerInvariant());
        return match.Success ? (match.Groups["attr"].Value, match.Groups["val"].Value) : null;
    }

    [GeneratedRegex(@"^groups\[(?<attr>value|display)\s+eq\s+""(?<val>(?:[^""\\]|\\.)*)""\]$")]
    private static partial Regex GroupFilterRegex();

    [GeneratedRegex(@"^members\[value\s+eq\s+""(?<id>(?:[^""\\]|\\.)*)""\]$", RegexOptions.IgnoreCase)]
    private static partial Regex MemberFilterRegex();

    [GeneratedRegex(@"^:claims\[type\s+eq\s+""(?<type>(?:[^""\\]|\\.)*)""\]$", RegexOptions.IgnoreCase)]
    private static partial Regex ClaimTypeFilterRegex();

    private async Task ValidateEmailAsync(string email, string? userId)
    {
        if (string.IsNullOrWhiteSpace(email)
            || email.Length > UserAdministration.EmailMaxLength
            || !MailAddress.TryCreate(email, out var parsed)
            || !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase)
            || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
        {
            throw ScimException.InvalidValue($"\"{email}\" is not a valid e-mail address.");
        }

        var byEmail = await _users.FindByEmailAsync(email);
        var byName = await _users.FindByNameAsync(email);
        if ((byEmail is not null && byEmail.Id != userId) || (byName is not null && byName.Id != userId))
        {
            throw new ScimException(StatusCodes.Status409Conflict, ScimConstants.ScimTypes.Uniqueness, $"The e-mail \"{email}\" is already used by another account.");
        }
    }

    private List<Claim> ValidateClaims(List<ScimClaim>? claims)
    {
        if (claims is null)
        {
            return new List<Claim>();
        }

        var errors = new Dictionary<string, string>();
        var validated = CustomClaims.Validate(claims.Select(c => new ClaimEntry { Type = c.Type ?? string.Empty, Value = c.Value ?? string.Empty }), errors);
        if (errors.Count > 0)
        {
            throw ScimException.InvalidValue(string.Join(" ", errors.Values));
        }

        return validated;
    }

    private async Task<List<string>> ResolveGroupsAsync(List<ScimGroupRef>? refs)
    {
        var groups = new List<string>();
        if (refs is null)
        {
            return groups;
        }

        foreach (var reference in refs)
        {
            Group? group = null;
            if (!string.IsNullOrWhiteSpace(reference.Value))
            {
                group = await _groups.FindByIdAsync(reference.Value);
            }

            if (group is null && !string.IsNullOrWhiteSpace(reference.Display))
            {
                group = await _groups.FindByNameAsync(reference.Display);
            }

            if (group?.Name is null)
            {
                throw new ScimException(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.NoTarget,
                    $"No group matches value \"{reference.Value}\" / display \"{reference.Display}\".");
            }

            if (!groups.Contains(group.Name, StringComparer.OrdinalIgnoreCase))
            {
                groups.Add(group.Name);
            }
        }

        return groups;
    }

    private async Task<List<User>> ResolveMembersAsync(List<ScimMember>? members)
    {
        var result = new List<User>();
        if (members is null)
        {
            return result;
        }

        foreach (var member in members)
        {
            if (string.IsNullOrWhiteSpace(member.Value))
            {
                continue;
            }

            result.Add(await FindUserAsync(member.Value));
        }

        return result;
    }

    private async Task<string> ValidateGroupNameAsync(string? name, string? groupId, string? currentName = null)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw ScimException.InvalidValue("The group displayName is required.");
        }

        if (GroupAdministration.IsBuiltIn(currentName) && !string.Equals(name, currentName, StringComparison.Ordinal))
        {
            throw new ScimException(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.Mutability, $"The built-in group \"{currentName}\" cannot be renamed.");
        }

        if (name.Length > GroupAdministration.NameMaxLength)
        {
            throw ScimException.InvalidValue($"The name is longer than {GroupAdministration.NameMaxLength} characters.");
        }

        if (!GroupAdministration.IsValidName(name))
        {
            throw ScimException.InvalidValue($"\"{name}\" is not a valid group name (letters, digits, '-', '_', '.').");
        }

        if (GroupAdministration.IsBuiltIn(name) || (await _groups.FindByNameAsync(name)) is { } other && other.Id != groupId)
        {
            throw new ScimException(StatusCodes.Status409Conflict, ScimConstants.ScimTypes.Uniqueness, $"The group \"{name}\" already exists.");
        }

        return name;
    }

    private async Task EnsureNotLastAdminAsync(User user, IEnumerable<string> currentGroups, IEnumerable<string> newGroups)
    {
        var isAdmin = currentGroups.Contains(DatabaseInitializer.AdminGroup, StringComparer.OrdinalIgnoreCase);
        var staysAdmin = newGroups.Contains(DatabaseInitializer.AdminGroup, StringComparer.OrdinalIgnoreCase);
        if (isAdmin && !staysAdmin && (await _users.GetUsersInRoleAsync(DatabaseInitializer.AdminGroup)).Count <= 1)
        {
            throw new ScimException(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.Mutability, "The Admins group cannot be removed from the last administrator.");
        }
    }
}
