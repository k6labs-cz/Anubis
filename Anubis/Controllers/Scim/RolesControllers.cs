using System.Text.Json.Nodes;

using k6labs.Anubis.Scim;
using k6labs.Anubis.Services;

using Microsoft.AspNetCore.Mvc;

namespace k6labs.Anubis.Controllers.Scim;

/// <summary>What the three role resources share: the list query and the required body.</summary>
public abstract class ScimRoleControllerBase : ScimControllerBase
{
    protected ScimListQuery Query(string? filter, string? sortBy, string? sortOrder, int? startIndex, int? count, string? attributes, string? excludedAttributes)
    {
        var (start, size) = Page(startIndex, count);
        return new ScimListQuery(filter, sortBy, sortOrder, start, size, attributes, excludedAttributes);
    }

    protected static T Body<T>(T? body, string resourceType) where T : class
        => body ?? throw ScimException.InvalidSyntax($"The request body with a {resourceType} is required.");

    protected IActionResult Created(JsonObject created, string endpoint)
    {
        Response.Headers.Location = $"{BaseUri}/api/{endpoint}/{created["id"]}";
        return ScimJson(created, StatusCodes.Status201Created);
    }
}

/// <summary>Tenants: /api/Tenants, at the default tenant only. A tenant's <c>name</c> is its identifier (and its SCIM id).</summary>
[Route("api/Tenants")]
public sealed class TenantsController(ScimRoleService scim) : ScimRoleControllerBase
{
    [HttpGet]
    public IActionResult List(
        [FromQuery] string? filter, [FromQuery] string? sortBy, [FromQuery] string? sortOrder,
        [FromQuery] int? startIndex, [FromQuery] int? count, [FromQuery] string? attributes, [FromQuery] string? excludedAttributes)
        => ScimJson(scim.ListTenants(Query(filter, sortBy, sortOrder, startIndex, count, attributes, excludedAttributes), BaseUri));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JsonObject? resource)
        => Created(await scim.CreateTenantAsync(Body(resource, "Tenant"), BaseUri), "Tenants");

    [HttpGet("{id}")]
    public IActionResult Get(string id) => ScimJson(scim.GetTenant(id, BaseUri));

    [HttpPut("{id}")]
    public async Task<IActionResult> Replace(string id, [FromBody] JsonObject? resource)
        => ScimJson(await scim.ReplaceTenantAsync(id, Body(resource, "Tenant"), BaseUri));

    [HttpPatch("{id}")]
    public async Task<IActionResult> Patch(string id, [FromBody] ScimPatchRequest? patch)
        => ScimJson(await scim.PatchTenantAsync(id, Body(patch, "PatchRequest"), BaseUri));

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        await scim.DeleteTenantAsync(id);
        return NoContent();
    }
}

/// <summary>The roles client applications define: /api/Roles.</summary>
[Route("api/Roles")]
public sealed class RolesController(ScimRoleService scim) : ScimRoleControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? filter, [FromQuery] string? sortBy, [FromQuery] string? sortOrder,
        [FromQuery] int? startIndex, [FromQuery] int? count, [FromQuery] string? attributes, [FromQuery] string? excludedAttributes)
        => ScimJson(await scim.ListRolesAsync(Query(filter, sortBy, sortOrder, startIndex, count, attributes, excludedAttributes), BaseUri));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JsonObject? resource)
        => Created(await scim.CreateRoleAsync(Body(resource, "Role"), BaseUri), "Roles");

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id) => ScimJson(await scim.GetRoleAsync(id, BaseUri));

    [HttpPut("{id}")]
    public async Task<IActionResult> Replace(string id, [FromBody] JsonObject? resource)
        => ScimJson(await scim.ReplaceRoleAsync(id, Body(resource, "Role"), BaseUri));

    [HttpPatch("{id}")]
    public async Task<IActionResult> Patch(string id, [FromBody] ScimPatchRequest? patch)
        => ScimJson(await scim.PatchRoleAsync(id, Body(patch, "PatchRequest"), BaseUri));

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        await scim.DeleteRoleAsync(id);
        return NoContent();
    }
}

/// <summary>Who has which role: /api/RoleAssignments. An assignment doesn't change: delete it and create another.</summary>
[Route("api/RoleAssignments")]
public sealed class RoleAssignmentsController(ScimRoleService scim) : ScimRoleControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? filter, [FromQuery] string? sortBy, [FromQuery] string? sortOrder,
        [FromQuery] int? startIndex, [FromQuery] int? count, [FromQuery] string? attributes, [FromQuery] string? excludedAttributes)
        => ScimJson(await scim.ListAssignmentsAsync(Query(filter, sortBy, sortOrder, startIndex, count, attributes, excludedAttributes), BaseUri));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JsonObject? resource)
        => Created(await scim.CreateAssignmentAsync(Body(resource, "RoleAssignment"), BaseUri), "RoleAssignments");

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id) => ScimJson(await scim.GetAssignmentAsync(id, BaseUri));

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        await scim.DeleteAssignmentAsync(id);
        return NoContent();
    }
}
