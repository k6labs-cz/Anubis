using System.Security.Claims;
using System.Text.Json.Nodes;

using k6labs.Anubis.Data.Entities;

namespace k6labs.Anubis.Scim;

/// <summary>
/// Builds SCIM resource representations (<see cref="JsonObject"/>) from Anubis entities, list responses
/// and error responses, and applies the "attributes" / "excludedAttributes" query parameters.
/// </summary>
public static class ScimResources
{
    public static JsonObject UserResource(User user, IEnumerable<string> roles, IReadOnlyDictionary<string, string> roleIds, IEnumerable<Claim> ownClaims, string baseUri)
    {
        var groups = new JsonArray();
        foreach (var role in roles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            if (!roleIds.TryGetValue(role, out var roleId))
            {
                continue;
            }

            groups.Add(new JsonObject
            {
                ["value"] = roleId,
                ["display"] = role,
                ["$ref"] = $"{baseUri}/api/Groups/{roleId}",
            });
        }

        var claims = new JsonArray();
        foreach (var claim in ownClaims.OrderBy(c => c.Type, StringComparer.Ordinal).ThenBy(c => c.Value, StringComparer.Ordinal))
        {
            claims.Add(new JsonObject { ["type"] = claim.Type, ["value"] = claim.Value });
        }

        var extension = new JsonObject
        {
            ["claims"] = claims,
            ["emailConfirmed"] = user.EmailConfirmed,
        };

        return new JsonObject
        {
            ["schemas"] = new JsonArray(ScimConstants.UserSchema, ScimConstants.UserExtensionSchema),
            ["id"] = user.Id,
            ["userName"] = user.UserName ?? string.Empty,
            ["displayName"] = user.DisplayName ?? user.UserName ?? string.Empty,
            ["name"] = new JsonObject { ["formatted"] = user.DisplayName ?? user.UserName ?? string.Empty },
            ["active"] = user.LockoutEnd is null || user.LockoutEnd <= DateTimeOffset.UtcNow,
            ["emails"] = new JsonArray(new JsonObject { ["value"] = user.Email ?? string.Empty, ["type"] = "work", ["primary"] = true }),
            ["phoneNumbers"] = user.PhoneNumber is { } phone
                ? new JsonArray(new JsonObject { ["value"] = phone, ["type"] = "work", ["primary"] = true })
                : new JsonArray(),
            ["groups"] = groups,
            [ScimConstants.UserExtensionSchema] = extension,
            ["meta"] = new JsonObject
            {
                ["resourceType"] = "User",
                ["created"] = user.CreatedAt.ToString("o"),
                ["location"] = $"{baseUri}/api/Users/{user.Id}",
            },
        };
    }

    public static JsonObject GroupResource(Role role, IReadOnlyList<(string Id, string? Display)> members, string baseUri)
    {
        var memberArray = new JsonArray();
        foreach (var member in members.OrderBy(m => m.Display, StringComparer.OrdinalIgnoreCase))
        {
            memberArray.Add(new JsonObject
            {
                ["value"] = member.Id,
                ["display"] = member.Display ?? string.Empty,
                ["$ref"] = $"{baseUri}/api/Users/{member.Id}",
            });
        }

        return new JsonObject
        {
            ["schemas"] = new JsonArray(ScimConstants.GroupSchema),
            ["id"] = role.Id,
            ["displayName"] = role.Name ?? string.Empty,
            ["members"] = memberArray,
            ["meta"] = new JsonObject
            {
                ["resourceType"] = "Group",
                ["location"] = $"{baseUri}/api/Groups/{role.Id}",
            },
        };
    }

    public static JsonObject ListResponse(int totalResults, int startIndex, IReadOnlyList<JsonObject> resources)
        => new()
        {
            ["schemas"] = new JsonArray(ScimConstants.ListResponseSchema),
            ["totalResults"] = totalResults,
            ["startIndex"] = startIndex,
            ["itemsPerPage"] = resources.Count,
            ["Resources"] = new JsonArray([.. resources]),
        };

    public static JsonObject Error(int status, string? scimType, string detail)
    {
        var error = new JsonObject
        {
            ["schemas"] = new JsonArray(ScimConstants.ErrorSchema),
            ["status"] = status.ToString(),
            ["detail"] = detail,
        };
        if (scimType is not null)
        {
            error["scimType"] = scimType;
        }

        return error;
    }

    /// <summary>Applies "attributes" / "excludedAttributes" (top-level attribute names, comma-separated). "schemas" and "id" are always kept.</summary>
    public static void ApplySelection(JsonObject resource, string? attributes, string? excludedAttributes)
    {
        var keep = string.IsNullOrWhiteSpace(attributes)
            ? null
            : attributes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exclude = string.IsNullOrWhiteSpace(excludedAttributes)
            ? null
            : excludedAttributes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (keep is null && exclude is null)
        {
            return;
        }

        foreach (var property in resource.ToList())
        {
            if (property.Key is "schemas" or "id")
            {
                continue;
            }

            if (exclude?.Contains(property.Key) == true || (keep is not null && !keep.Contains(property.Key)))
            {
                resource.Remove(property.Key);
            }
        }
    }
}
