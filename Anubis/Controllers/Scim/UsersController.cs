using k6labs.Anubis.Scim;
using k6labs.Anubis.Services;

using Microsoft.AspNetCore.Mvc;

namespace k6labs.Anubis.Controllers.Scim;

/// <summary>SCIM Users resource (RFC 7643 section 4.1): /api/Users.</summary>
[Route("api/Users")]
public sealed class UsersController : ScimControllerBase
{
    private readonly ScimService _scim;

    public UsersController(ScimService scim)
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
        return ScimJson(await _scim.ListUsersAsync(filter, sortBy, sortOrder, start, size, attributes, excludedAttributes, BaseUri));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ScimUserResource? resource)
    {
        if (resource is null)
        {
            throw ScimException.InvalidSyntax("The request body with a User resource is required.");
        }

        var created = await _scim.CreateUserAsync(resource, BaseUri);
        Response.Headers.Location = $"{BaseUri}/api/Users/{created["id"]}";
        return ScimJson(created, StatusCodes.Status201Created);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, [FromQuery] string? attributes, [FromQuery] string? excludedAttributes)
        => ScimJson(await _scim.GetUserAsync(id, attributes, excludedAttributes, BaseUri));

    [HttpPut("{id}")]
    public async Task<IActionResult> Replace(string id, [FromBody] ScimUserResource? resource)
    {
        if (resource is null)
        {
            throw ScimException.InvalidSyntax("The request body with a User resource is required.");
        }

        return ScimJson(await _scim.ReplaceUserAsync(id, resource, BaseUri));
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Patch(string id, [FromBody] ScimPatchRequest? patch)
    {
        if (patch is null)
        {
            throw ScimException.InvalidSyntax("The request body with a PatchRequest is required.");
        }

        return ScimJson(await _scim.PatchUserAsync(id, patch, BaseUri));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        await _scim.DeleteUserAsync(id);
        return NoContent();
    }
}
