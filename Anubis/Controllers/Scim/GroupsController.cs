using k6labs.Anubis.Scim;
using k6labs.Anubis.Services;

using Microsoft.AspNetCore.Mvc;

namespace k6labs.Anubis.Controllers.Scim;

/// <summary>SCIM Groups resource (RFC 7643 section 4.2): /api/Groups. A group is an Anubis role, its members are the users in the role.</summary>
[Route("api/Groups")]
public sealed class GroupsController : ScimControllerBase
{
    private readonly ScimService _scim;

    public GroupsController(ScimService scim)
    {
        _scim = scim;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? filter, [FromQuery] string? sortBy, [FromQuery] string? sortOrder,
        [FromQuery] int? startIndex, [FromQuery] int? count,
        [FromQuery] string? attributes, [FromQuery] string? excludedAttributes)
    {
        var (start, size) = Page(startIndex, count);
        return ScimJson(await _scim.ListGroupsAsync(filter, sortBy, sortOrder, start, size, attributes, excludedAttributes, BaseUri));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ScimGroupResource? resource)
    {
        if (resource is null)
        {
            throw ScimException.InvalidSyntax("The request body with a Group resource is required.");
        }

        var created = await _scim.CreateGroupAsync(resource, BaseUri);
        Response.Headers.Location = $"{BaseUri}/api/Groups/{created["id"]}";
        return ScimJson(created, StatusCodes.Status201Created);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, [FromQuery] string? attributes, [FromQuery] string? excludedAttributes)
        => ScimJson(await _scim.GetGroupAsync(id, attributes, excludedAttributes, BaseUri));

    [HttpPut("{id}")]
    public async Task<IActionResult> Replace(string id, [FromBody] ScimGroupResource? resource)
    {
        if (resource is null)
        {
            throw ScimException.InvalidSyntax("The request body with a Group resource is required.");
        }

        return ScimJson(await _scim.ReplaceGroupAsync(id, resource, BaseUri));
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Patch(string id, [FromBody] ScimPatchRequest? patch)
    {
        if (patch is null)
        {
            throw ScimException.InvalidSyntax("The request body with a PatchRequest is required.");
        }

        return ScimJson(await _scim.PatchGroupAsync(id, patch, BaseUri));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        await _scim.DeleteGroupAsync(id);
        return NoContent();
    }
}
