using Microsoft.AspNetCore.Components.Server.Circuits;

namespace k6labs.Anubis.Tenancy;

/// <summary>
/// First in the pipeline: finds the tenant of the request by its host name and makes it the current
/// one (<see cref="TenantContext"/>) for everything that follows, including the static files of a
/// tenant's appearance and the error page.
/// </summary>
public sealed class TenantMiddleware(RequestDelegate next, TenantCatalog catalog)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using (TenantContext.Use(catalog.Resolve(context.Request.Host.Host)))
        {
            await next(context);
        }
    }
}

/// <summary>
/// An interactive page lives in a circuit, after the request that opened it is gone: the circuit
/// remembers that request's tenant and makes it the current one for everything it does.
/// </summary>
public sealed class TenantCircuitHandler : CircuitHandler
{
    private readonly Tenant _tenant = TenantContext.Current;

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next)
        => async context =>
        {
            using (TenantContext.Use(_tenant))
            {
                await next(context);
            }
        };
}
