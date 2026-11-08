namespace k6labs.Anubis.Services.Activity;

/// <summary>
/// IP address, user agent and signed-in user id of whoever triggers an action, for the
/// account history. Filled per HTTP request (middleware) and, for interactive pages, once
/// when the Blazor circuit opens (<see cref="ClientInfoCircuitHandler"/>), because
/// HttpContext is not available inside a circuit afterwards.
/// </summary>
public sealed class ClientInfo
{
    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public string? UserId { get; private set; }

    public void CaptureFrom(HttpContext? context)
    {
        if (context is null)
        {
            return;
        }

        IpAddress = context.Connection.RemoteIpAddress?.ToString();
        var agent = context.Request.Headers.UserAgent.ToString();
        UserAgent = string.IsNullOrEmpty(agent) ? null : agent[..Math.Min(agent.Length, 512)];
        UserId = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            : null;
    }
}

/// <summary>Captures the client info of the request that opens a Blazor circuit.</summary>
public sealed class ClientInfoCircuitHandler : Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler
{
    public ClientInfoCircuitHandler(ClientInfo clientInfo, IHttpContextAccessor accessor)
    {
        clientInfo.CaptureFrom(accessor.HttpContext);
    }
}
