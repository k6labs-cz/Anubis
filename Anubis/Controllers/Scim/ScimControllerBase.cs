using System.Text.Json.Nodes;

using k6labs.Anubis.Scim;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;

namespace k6labs.Anubis.Controllers.Scim;

/// <summary>
/// Base of the SCIM 2.0 API controllers under /api/: bearer access token with the API scope
/// (client credentials from /token), rate limited, answering application/scim+json, with
/// <see cref="ScimException"/>s rendered as SCIM error responses (RFC 7644 section 3.12).
/// </summary>
[Authorize(Policy = PolicyName)]
[IgnoreAntiforgeryToken]
[EnableRateLimiting("api")]
[ServiceFilter(typeof(ScimExceptionFilter))]
public abstract class ScimControllerBase : Controller
{
    /// <summary>Authorization policy: an authenticated bearer token that carries the Anubis API scope.</summary>
    public const string PolicyName = "scim-api";

    /// <summary>Scheme + host (+ path base) of this request, for the "location" of resources.</summary>
    protected string BaseUri => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    protected IActionResult ScimJson(JsonObject payload, int status = StatusCodes.Status200OK)
        => new ContentResult
        {
            StatusCode = status,
            ContentType = ScimConstants.MediaType,
            Content = payload.ToJsonString(),
        };

    /// <summary>1-based "startIndex" (default 1) and "count" (default 50, at most 200) of a list request.</summary>
    protected (int StartIndex, int Count) Page(int? startIndex, int? count)
        => (startIndex is > 0 ? startIndex.Value : 1,
            count is null ? ScimConstants.DefaultCount : Math.Clamp(count.Value, 1, ScimConstants.MaxCount));
}

/// <summary>Renders <see cref="ScimException"/> as a SCIM error response (RFC 7644 section 3.12).</summary>
public sealed class ScimExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ScimException scim)
        {
            return;
        }

        context.Result = new ContentResult
        {
            StatusCode = scim.Status,
            ContentType = ScimConstants.MediaType,
            Content = ScimResources.Error(scim.Status, scim.ScimType, scim.Message).ToJsonString(),
        };
        context.ExceptionHandled = true;
    }
}
