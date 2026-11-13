using System.Text.Json.Nodes;

using k6labs.Anubis.Scim;
using k6labs.Anubis.Tenancy;

namespace k6labs.Anubis.Services;

/// <summary>
/// Tenants, roles and role assignments in the SCIM API (/api/Tenants, /api/Roles, /api/RoleAssignments),
/// over the same <see cref="TenantAdministration"/> and <see cref="RoleAdministration"/> as the admin UI.
/// They are Anubis's own resource types (SCIM has none for them), with the usual list (filter, sort,
/// paging), get, create and delete. A tenant's display name and host names and a role's description can
/// be replaced (PUT) or patched; everything else is fixed at creation, because applications know tenants
/// and roles by their names. Roles and assignments are those of the tenant the API is called at; tenants
/// are managed at the default tenant only.
/// </summary>
public sealed class ScimRoleService
{
    private readonly RoleAdministration _roles;
    private readonly TenantAdministration _tenants;

    public ScimRoleService(RoleAdministration roles, TenantAdministration tenants)
    {
        _roles = roles;
        _tenants = tenants;
    }

    // ---------------------------------------------------------------- tenants

    public JsonObject ListTenants(ScimListQuery query, string baseUri)
        => List(Catalog().List().Select(t => Tenant(t, baseUri)), query, "name", ["id", "name", "displayname", "meta.created"]);

    public JsonObject GetTenant(string id, string baseUri)
        => Tenant(Catalog().Find(id) ?? throw ScimException.NotFound("Tenant", id), baseUri);

    /// <summary>With <c>adminEmail</c> and <c>adminPassword</c> the new tenant gets its first administrator.</summary>
    public Task<JsonObject> CreateTenantAsync(JsonObject resource, string baseUri)
        => RunAsync(async () =>
        {
            var (email, password) = (Text(resource, "adminEmail"), Text(resource, "adminPassword"));
            if ((email is null) != (password is null))
            {
                throw ScimException.InvalidValue("The attributes \"adminEmail\" and \"adminPassword\" go together.");
            }

            var administrator = email is null ? null : new TenantAdministrator(email, password!);
            return Tenant(await Catalog().CreateAsync(Text(resource, "name") ?? Text(resource, "id"), Text(resource, "displayName"), Hosts(resource), administrator), baseUri);
        });

    public Task<JsonObject> ReplaceTenantAsync(string id, JsonObject resource, string baseUri)
        => RunAsync(async () =>
        {
            if ((Text(resource, "name") ?? id) != id)
            {
                throw new ScimException(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.Mutability, "A tenant's name can't change.");
            }

            Changeable(id);
            return Tenant(await Catalog().UpdateAsync(id, Text(resource, "displayName"), Hosts(resource) ?? []), baseUri);
        }, "Tenant", id);

    /// <summary>"replace"/"add"/"remove" of <c>displayName</c> and <c>hosts</c> (the whole list).</summary>
    public Task<JsonObject> PatchTenantAsync(string id, ScimPatchRequest patch, string baseUri)
        => RunAsync(async () =>
        {
            var tenant = Changeable(id);
            var (displayName, hosts) = (tenant.DisplayName, tenant.Hosts);
            if (patch.Operations is null || patch.Operations.Count == 0)
            {
                throw ScimException.InvalidSyntax("The PATCH request must contain at least one operation.");
            }

            foreach (var operation in patch.Operations)
            {
                var path = operation.Path?.Trim();

                // What changes: the attribute of the path, or every attribute of a value object without a path.
                IEnumerable<KeyValuePair<string, JsonNode?>> changes = operation.Op?.ToLowerInvariant() switch
                {
                    "remove" when !string.IsNullOrEmpty(path) => [new(path, null)],
                    "add" or "replace" when !string.IsNullOrEmpty(path) => [new(path, operation.Value)],
                    "add" or "replace" when operation.Value is JsonObject values => values,
                    _ => throw ScimException.InvalidSyntax("The operation needs \"op\" (add, replace, remove) and a path or a value object."),
                };

                foreach (var (attribute, value) in changes)
                {
                    if (string.Equals(attribute, "displayName", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = value is JsonValue text && text.TryGetValue<string>(out var name) ? name : null;
                    }
                    else if (string.Equals(attribute, "hosts", StringComparison.OrdinalIgnoreCase))
                    {
                        hosts = Hosts(new JsonObject { ["hosts"] = value?.DeepClone() }) ?? [];
                    }
                    else
                    {
                        throw new ScimException(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.Mutability, "Only \"displayName\" and \"hosts\" can be changed.");
                    }
                }
            }

            return Tenant(await Catalog().UpdateAsync(id, displayName, hosts), baseUri);
        }, "Tenant", id);

    public Task DeleteTenantAsync(string id)
        => RunAsync(async () => { await Catalog().DeleteAsync(id); return new JsonObject(); }, "Tenant", id);

    /// <summary>Tenants are managed from the default tenant only: to any other, the resource type does not exist.</summary>
    private TenantAdministration Catalog()
        => TenantContext.Current.IsDefault ? _tenants : throw new ScimException(StatusCodes.Status404NotFound, null, "Tenants are managed in the default tenant.");

    /// <summary>The default tenant is listed, but there is nothing about it to change here.</summary>
    private Tenant Changeable(string id)
        => Catalog().Find(id) switch
        {
            null => throw ScimException.NotFound("Tenant", id),
            { IsDefault: true } => throw new ScimException(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.Mutability, "The default tenant can't be changed."),
            var tenant => tenant,
        };

    /// <summary>The "hosts" attribute: an array of host names (or one string of them); null when absent.</summary>
    private static List<string>? Hosts(JsonObject resource)
        => resource.FirstOrDefault(p => string.Equals(p.Key, "hosts", StringComparison.OrdinalIgnoreCase)).Value switch
        {
            JsonArray array => array.Select(item => item is JsonValue value && value.TryGetValue<string>(out var host) ? host : string.Empty).ToList(),
            JsonValue single when single.TryGetValue<string>(out var text) => [.. text.Split([' ', ',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            _ => null,
        };

    // ---------------------------------------------------------------- roles

    public async Task<JsonObject> ListRolesAsync(ScimListQuery query, string baseUri)
        => List((await _roles.ListRolesAsync()).Select(r => Role(r, baseUri)), query, "name", ["id", "name", "client"]);

    public async Task<JsonObject> GetRoleAsync(string id, string baseUri)
        => Role(await _roles.FindRoleAsync(id) ?? throw ScimException.NotFound("Role", id), baseUri);

    public Task<JsonObject> CreateRoleAsync(JsonObject resource, string baseUri)
        => RunAsync(async () => Role(await _roles.CreateRoleAsync(Text(resource, "client"), Text(resource, "name"), Text(resource, "description")), baseUri));

    public Task<JsonObject> ReplaceRoleAsync(string id, JsonObject resource, string baseUri)
        => RunAsync(async () =>
        {
            var current = await _roles.FindRoleAsync(id) ?? throw ScimException.NotFound("Role", id);
            if ((Text(resource, "name") ?? current.Name) != current.Name || (Text(resource, "client") ?? current.ClientId) != current.ClientId)
            {
                throw new ScimException(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.Mutability, "A role's name and client can't change.");
            }

            return Role(await _roles.DescribeRoleAsync(id, Text(resource, "description")), baseUri);
        }, "Role", id);

    public Task<JsonObject> PatchRoleAsync(string id, ScimPatchRequest patch, string baseUri)
        => RunAsync(async () => Role(await _roles.DescribeRoleAsync(id, Patched(patch, "description")), baseUri), "Role", id);

    public Task DeleteRoleAsync(string id)
        => RunAsync(async () => { await _roles.DeleteRoleAsync(id); return new JsonObject(); }, "Role", id);

    // ---------------------------------------------------------------- assignments

    public async Task<JsonObject> ListAssignmentsAsync(ScimListQuery query, string baseUri)
        => List((await _roles.ListAssignmentsAsync()).Select(a => Assignment(a, baseUri)), query, "claim", ["id", "claim", "role.client", "role.name", "meta.created"]);

    public async Task<JsonObject> GetAssignmentAsync(string id, string baseUri)
        => Assignment(await _roles.FindAssignmentAsync(id) ?? throw ScimException.NotFound("RoleAssignment", id), baseUri);

    /// <summary>The role by its id (<c>role.value</c>) or by <c>role.client</c> + <c>role.name</c>; one of <c>user.value</c> and <c>group.value</c>.</summary>
    public Task<JsonObject> CreateAssignmentAsync(JsonObject resource, string baseUri)
        => RunAsync(async () =>
        {
            var role = resource["role"] as JsonObject ?? throw ScimException.InvalidValue("The attribute \"role\" is required.");
            var roleId = Text(role, "value");
            if (roleId is null)
            {
                var (client, name) = (Text(role, "client"), Text(role, "name"));
                roleId = (await _roles.ListRolesAsync(client)).FirstOrDefault(r => r.Name == name)?.Id
                    ?? throw ScimException.InvalidValue($"The role \"{name}\" of the client \"{client}\" was not found.");
            }

            var created = await _roles.AssignAsync(roleId, Text(resource["user"] as JsonObject, "value"), Text(resource["group"] as JsonObject, "value"));
            return Assignment(created, baseUri);
        });

    public Task DeleteAssignmentAsync(string id)
        => RunAsync(async () => { await _roles.RemoveAssignmentAsync(id); return new JsonObject(); }, "RoleAssignment", id);

    // ---------------------------------------------------------------- resources

    private static JsonObject Tenant(Tenant tenant, string baseUri) => new()
    {
        ["schemas"] = new JsonArray(ScimConstants.TenantSchema),
        ["id"] = tenant.Id,
        ["name"] = tenant.Id,
        ["displayName"] = tenant.DisplayName ?? string.Empty,
        ["hosts"] = new JsonArray([.. tenant.Hosts.Select(host => (JsonNode)host)]),
        ["meta"] = new JsonObject
        {
            ["resourceType"] = "Tenant",
            ["created"] = tenant.CreatedAt.ToString("o"),
            ["location"] = $"{baseUri}/api/Tenants/{tenant.Id}",
        },
    };

    private static JsonObject Role(RoleSummary role, string baseUri) => new()
    {
        ["schemas"] = new JsonArray(ScimConstants.RoleSchema),
        ["id"] = role.Id,
        ["client"] = role.ClientId,
        ["name"] = role.Name,
        ["description"] = role.Description ?? string.Empty,
        ["meta"] = new JsonObject { ["resourceType"] = "Role", ["location"] = $"{baseUri}/api/Roles/{role.Id}" },
    };

    private static JsonObject Assignment(AssignmentSummary assignment, string baseUri)
    {
        var resource = new JsonObject
        {
            ["schemas"] = new JsonArray(ScimConstants.RoleAssignmentSchema),
            ["id"] = assignment.Id,
            ["role"] = new JsonObject
            {
                ["value"] = assignment.RoleId,
                ["client"] = assignment.ClientId,
                ["name"] = assignment.RoleName,
                ["$ref"] = $"{baseUri}/api/Roles/{assignment.RoleId}",
            },
            // What the application reads in the "roles" claim.
            ["claim"] = assignment.Claim,
        };
        if (assignment.UserId is not null)
        {
            resource["user"] = new JsonObject { ["value"] = assignment.UserId, ["display"] = assignment.UserName ?? string.Empty, ["$ref"] = $"{baseUri}/api/Users/{assignment.UserId}" };
        }

        if (assignment.GroupId is not null)
        {
            resource["group"] = new JsonObject { ["value"] = assignment.GroupId, ["display"] = assignment.GroupName ?? string.Empty, ["$ref"] = $"{baseUri}/api/Groups/{assignment.GroupId}" };
        }

        resource["meta"] = new JsonObject
        {
            ["resourceType"] = "RoleAssignment",
            ["created"] = assignment.CreatedAt.ToString("o"),
            ["location"] = $"{baseUri}/api/RoleAssignments/{assignment.Id}",
        };
        return resource;
    }

    // ---------------------------------------------------------------- helpers

    private static JsonObject List(IEnumerable<JsonObject> resources, ScimListQuery query, string defaultKey, string[] sortable)
    {
        var terms = ScimFilter.Parse(query.Filter);
        var filtered = resources.Where(r => ScimFilter.Matches(r, terms)).ToList();
        var sorted = ScimService.SortResources(filtered, query.SortBy, query.SortOrder, defaultKey, sortable);
        var page = sorted.Skip(query.StartIndex - 1).Take(query.Count).ToList();
        foreach (var resource in page)
        {
            ScimResources.ApplySelection(resource, query.Attributes, query.ExcludedAttributes);
        }

        return ScimResources.ListResponse(sorted.Count, query.StartIndex, page);
    }

    /// <summary>Runs a change; what <see cref="RoleAdministration"/> or <see cref="TenantAdministration"/> refuses becomes a SCIM error.</summary>
    private static async Task<JsonObject> RunAsync(Func<Task<JsonObject>> action, string? resourceType = null, string? id = null)
    {
        try
        {
            return await action();
        }
        catch (RoleAdministrationException e)
        {
            throw e.Error switch
            {
                RoleError.Conflict => new ScimException(StatusCodes.Status409Conflict, ScimConstants.ScimTypes.Uniqueness, e.Message),
                RoleError.NotFound when resourceType is not null => ScimException.NotFound(resourceType, id ?? string.Empty),
                _ => ScimException.InvalidValue(e.Message),
            };
        }
        catch (TenantAdministrationException e)
        {
            throw e.Error switch
            {
                TenantError.Conflict => new ScimException(StatusCodes.Status409Conflict, ScimConstants.ScimTypes.Uniqueness, e.Message),
                TenantError.NotFound when resourceType is not null => ScimException.NotFound(resourceType, id ?? string.Empty),
                _ => ScimException.InvalidValue(e.Message),
            };
        }
    }

    private static string? Text(JsonObject? resource, string attribute)
        => resource?.FirstOrDefault(p => string.Equals(p.Key, attribute, StringComparison.OrdinalIgnoreCase)).Value is JsonValue value
            && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    /// <summary>The one attribute a PATCH may set: "replace"/"add" with the path, or with a value object holding it; "remove" clears it.</summary>
    private static string? Patched(ScimPatchRequest patch, string attribute)
    {
        if (patch.Operations is null || patch.Operations.Count == 0)
        {
            throw ScimException.InvalidSyntax("The PATCH request must contain at least one operation.");
        }

        string? result = null;
        foreach (var operation in patch.Operations)
        {
            var op = operation.Op?.ToLowerInvariant();
            var path = operation.Path?.Trim();
            if (op is "add" or "replace" && string.Equals(path, attribute, StringComparison.OrdinalIgnoreCase))
            {
                result = operation.Value is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            }
            else if (op is "add" or "replace" && string.IsNullOrEmpty(path) && operation.Value is JsonObject values && values.Count == 1 && Text(values, attribute) is { } inner)
            {
                result = inner;
            }
            else if (op == "remove" && string.Equals(path, attribute, StringComparison.OrdinalIgnoreCase))
            {
                result = null;
            }
            else
            {
                throw new ScimException(StatusCodes.Status400BadRequest, ScimConstants.ScimTypes.Mutability, $"Only \"{attribute}\" can be changed.");
            }
        }

        return result;
    }
}

/// <summary>The query of a SCIM list request.</summary>
public sealed record ScimListQuery(string? Filter, string? SortBy, string? SortOrder, int StartIndex, int Count, string? Attributes, string? ExcludedAttributes);
