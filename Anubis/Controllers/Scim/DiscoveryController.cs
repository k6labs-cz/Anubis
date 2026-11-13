using System.Text.Json.Nodes;

using k6labs.Anubis.Options;
using k6labs.Anubis.Scim;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace k6labs.Anubis.Controllers.Scim;

/// <summary>
/// SCIM discovery endpoints (RFC 7643 section 4): /api/ServiceProviderConfig, /api/ResourceTypes
/// and /api/Schemas describe what the API under /api/ supports.
/// </summary>
[Route("api")]
public sealed class DiscoveryController : ScimControllerBase
{
    private readonly AnubisOptions _options;

    public DiscoveryController(IOptionsMonitor<AnubisOptions> options)
    {
        _options = options.CurrentValue;
    }

    [HttpGet("ServiceProviderConfig")]
    public IActionResult ServiceProviderConfig()
        => ScimJson(new JsonObject
        {
            ["schemas"] = new JsonArray(ScimConstants.ServiceProviderConfigSchema),
            ["patch"] = new JsonObject { ["supported"] = true },
            ["bulk"] = new JsonObject { ["supported"] = false, ["maxOperations"] = 0, ["maxPayloadSize"] = 0 },
            ["filter"] = new JsonObject { ["supported"] = true, ["maxResults"] = ScimConstants.MaxCount },
            ["changePassword"] = new JsonObject { ["supported"] = false },
            ["sort"] = new JsonObject { ["supported"] = true },
            ["etag"] = new JsonObject { ["supported"] = false },
            ["authenticationSchemes"] = new JsonArray(new JsonObject
            {
                ["type"] = "oauth2",
                ["name"] = "OAuth 2.0 bearer token (client credentials grant on /token)",
                ["description"] = $"The access token must carry the \"{_options.ApiScope}\" scope.",
                ["specUri"] = "https://www.rfc-editor.org/rfc/rfc6749",
                ["documentationUri"] = $"{BaseUri}/token",
            }),
            ["meta"] = new JsonObject
            {
                ["resourceType"] = "ServiceProviderConfig",
                ["location"] = $"{BaseUri}/api/ServiceProviderConfig",
            },
        });

    [HttpGet("ResourceTypes")]
    public IActionResult ResourceTypes()
    {
        var types = AllResourceTypes();
        return ScimJson(ScimResources.ListResponse(types.Count, 1, types));
    }

    [HttpGet("ResourceTypes/{id}")]
    public IActionResult ResourceType(string id)
    {
        var resource = AllResourceTypes().FirstOrDefault(t => string.Equals(t["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));
        return resource is null
            ? ScimJson(ScimResources.Error(StatusCodes.Status404NotFound, null, $"Resource type \"{id}\" was not found."), StatusCodes.Status404NotFound)
            : ScimJson(resource);
    }

    private List<JsonObject> AllResourceTypes() =>
    [
        ResourceType("User", "/api/Users", ScimConstants.UserSchema, "An Anubis account (the e-mail address is the userName)."),
        ResourceType("Group", "/api/Groups", ScimConstants.GroupSchema, "An Anubis group; its members are the users in the group."),
        ResourceType("Tenant", "/api/Tenants", ScimConstants.TenantSchema, "A tenant: an organization with its own database of users, groups, applications and roles. Managed at the default tenant only."),
        ResourceType("Role", "/api/Roles", ScimConstants.RoleSchema, "A role a client application defines for itself."),
        ResourceType("RoleAssignment", "/api/RoleAssignments", ScimConstants.RoleAssignmentSchema, "A role given to a user or a group."),
    ];

    [HttpGet("Schemas")]
    public IActionResult Schemas()
    {
        var schemas = AllSchemas();
        return ScimJson(ScimResources.ListResponse(schemas.Count, 1, schemas));
    }

    [HttpGet("Schemas/{id}")]
    public IActionResult Schema(string id)
    {
        var schema = AllSchemas()
            .FirstOrDefault(s => string.Equals(s["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s["name"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));
        return schema is null
            ? ScimJson(ScimResources.Error(StatusCodes.Status404NotFound, null, $"Schema \"{id}\" was not found."), StatusCodes.Status404NotFound)
            : ScimJson(schema);
    }

    private JsonObject ResourceType(string name, string endpoint, string schema, string description)
        => new()
        {
            ["schemas"] = new JsonArray(ScimConstants.ResourceTypeSchema),
            ["id"] = name,
            ["name"] = name,
            ["endpoint"] = endpoint,
            ["schema"] = schema,
            ["description"] = description,
            ["meta"] = new JsonObject { ["resourceType"] = "ResourceType", ["location"] = $"{BaseUri}/api/ResourceTypes/{name}" },
        };

    private static JsonObject Attribute(string name, string type, bool multiValued = false, bool required = false, string? description = null)
    {
        var attribute = new JsonObject
        {
            ["name"] = name,
            ["type"] = type,
            ["multiValued"] = multiValued,
            ["required"] = required,
        };
        if (description is not null)
        {
            attribute["description"] = description;
        }

        return attribute;
    }

    private JsonObject UserSchema()
        => new()
        {
            ["schemas"] = new JsonArray(ScimConstants.SchemaSchema),
            ["id"] = ScimConstants.UserSchema,
            ["name"] = "User",
            ["description"] = "An Anubis account. userName is the e-mail address (also the sign-in name); active maps to the account lockout; groups are the user's groups; custom claims live in the Anubis extension schema.",
            ["attributes"] = new JsonArray(
                Attribute("id", "string", description: "Server-generated identifier."),
                Attribute("userName", "string", required: true, description: "The e-mail address, also the sign-in name."),
                Attribute("name", "complex", description: "Only name.formatted is stored (as the display name)."),
                Attribute("displayName", "string"),
                Attribute("active", "boolean", description: "false locks the account out."),
                Attribute("password", "string", description: "Write-only; set on create or via PATCH/PUT."),
                Attribute("emails", "complex", multiValued: true, description: "The primary e-mail is the account e-mail."),
                Attribute("phoneNumbers", "complex", multiValued: true),
                Attribute("groups", "complex", multiValued: true, description: "The user's groups: value = group id, display = group name."),
                Attribute("meta", "complex"),
                Attribute(ScimConstants.UserExtensionSchema, "complex", description: "Anubis extension: claims (custom claims) and emailConfirmed.")),
            ["meta"] = new JsonObject { ["resourceType"] = "Schema", ["location"] = $"{BaseUri}/api/Schemas/{ScimConstants.UserSchema}" },
        };

    private List<JsonObject> AllSchemas() =>
        [UserSchema(), GroupSchema(), UserExtension(), TenantSchema(), RoleSchema(), RoleAssignmentSchema(), ListResponseSchema(), PatchRequestSchema(), ErrorSchema()];

    private JsonObject TenantSchema()
        => new()
        {
            ["schemas"] = new JsonArray(ScimConstants.SchemaSchema),
            ["id"] = ScimConstants.TenantSchema,
            ["name"] = "Tenant",
            ["description"] = "A tenant: its own database, settings and address. The other resources of this API are those of the tenant the API is called at.",
            ["attributes"] = new JsonArray(
                Attribute("id", "string", description: "The same as name."),
                Attribute("name", "string", required: true, description: "The identifier: lowercase letters, digits and dashes. Immutable; it is the tenant's subdomain and the \"tenant\" claim of its tokens."),
                Attribute("displayName", "string"),
                Attribute("hosts", "string", multiValued: true, description: "Host names of the tenant's own, besides its subdomain."),
                Attribute("adminEmail", "string", description: "Write-only, when creating: the e-mail of the tenant's first administrator."),
                Attribute("adminPassword", "string", description: "Write-only, when creating: the administrator's password."),
                Attribute("meta", "complex")),
            ["meta"] = new JsonObject { ["resourceType"] = "Schema", ["location"] = $"{BaseUri}/api/Schemas/{ScimConstants.TenantSchema}" },
        };

    private JsonObject RoleSchema()
        => new()
        {
            ["schemas"] = new JsonArray(ScimConstants.SchemaSchema),
            ["id"] = ScimConstants.RoleSchema,
            ["name"] = "Role",
            ["description"] = "A role of a client application, e.g. Administrator of the client \"osiris\".",
            ["attributes"] = new JsonArray(
                Attribute("id", "string"),
                Attribute("client", "string", required: true, description: "The application's client_id. Immutable."),
                Attribute("name", "string", required: true, description: "What the application checks: no colon, no spaces. Immutable."),
                Attribute("description", "string"),
                Attribute("meta", "complex")),
            ["meta"] = new JsonObject { ["resourceType"] = "Schema", ["location"] = $"{BaseUri}/api/Schemas/{ScimConstants.RoleSchema}" },
        };

    private JsonObject RoleAssignmentSchema()
        => new()
        {
            ["schemas"] = new JsonArray(ScimConstants.SchemaSchema),
            ["id"] = ScimConstants.RoleAssignmentSchema,
            ["name"] = "RoleAssignment",
            ["description"] = "A role given to a user or a group. Immutable: delete it and create another.",
            ["attributes"] = new JsonArray(
                Attribute("id", "string"),
                Attribute("role", "complex", required: true, description: "value = the role's id; or client + name to find it."),
                Attribute("user", "complex", description: "value = user id. Exactly one of user and group."),
                Attribute("group", "complex", description: "value = group id; the group's members have the role."),
                Attribute("claim", "string", description: "Read-only: the value in the \"roles\" claim (the role's name)."),
                Attribute("meta", "complex")),
            ["meta"] = new JsonObject { ["resourceType"] = "Schema", ["location"] = $"{BaseUri}/api/Schemas/{ScimConstants.RoleAssignmentSchema}" },
        };

    private JsonObject GroupSchema()
        => new()
        {
            ["schemas"] = new JsonArray(ScimConstants.SchemaSchema),
            ["id"] = ScimConstants.GroupSchema,
            ["name"] = "Group",
            ["description"] = "An Anubis group; members are the users in the group (value = user id, display = e-mail).",
            ["attributes"] = new JsonArray(
                Attribute("id", "string"),
                Attribute("displayName", "string", required: true, description: "The group name."),
                Attribute("members", "complex", multiValued: true),
                Attribute("meta", "complex")),
            ["meta"] = new JsonObject { ["resourceType"] = "Schema", ["location"] = $"{BaseUri}/api/Schemas/{ScimConstants.GroupSchema}" },
        };

    private JsonObject UserExtension()
        => new()
        {
            ["schemas"] = new JsonArray(ScimConstants.SchemaSchema),
            ["id"] = ScimConstants.UserExtensionSchema,
            ["name"] = "AnubisUserExtension",
            ["description"] = "Anubis-specific user attributes.",
            ["attributes"] = new JsonArray(
                Attribute("claims", "complex", multiValued: true, description: "Custom claims (type/value) set directly on the user."),
                Attribute("emailConfirmed", "boolean")),
            ["meta"] = new JsonObject { ["resourceType"] = "Schema", ["location"] = $"{BaseUri}/api/Schemas/{ScimConstants.UserExtensionSchema}" },
        };

    private static JsonObject ListResponseSchema()
        => new()
        {
            ["schemas"] = new JsonArray(ScimConstants.SchemaSchema),
            ["id"] = ScimConstants.ListResponseSchema,
            ["name"] = "ListResponse",
            ["attributes"] = new JsonArray(
                Attribute("totalResults", "integer"),
                Attribute("startIndex", "integer"),
                Attribute("itemsPerPage", "integer"),
                Attribute("Resources", "complex", multiValued: true)),
        };

    private static JsonObject PatchRequestSchema()
        => new()
        {
            ["schemas"] = new JsonArray(ScimConstants.SchemaSchema),
            ["id"] = ScimConstants.PatchRequestSchema,
            ["name"] = "PatchRequest",
            ["attributes"] = new JsonArray(
                Attribute("Operations", "complex", multiValued: true, required: true)),
        };

    private static JsonObject ErrorSchema()
        => new()
        {
            ["schemas"] = new JsonArray(ScimConstants.SchemaSchema),
            ["id"] = ScimConstants.ErrorSchema,
            ["name"] = "Error",
            ["attributes"] = new JsonArray(
                Attribute("status", "string"),
                Attribute("scimType", "string"),
                Attribute("detail", "string")),
        };
}
