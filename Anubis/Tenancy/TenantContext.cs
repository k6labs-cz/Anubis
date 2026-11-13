namespace k6labs.Anubis.Tenancy;

/// <summary>
/// The tenant the running code works for. An HTTP request gets it from its host name
/// (<see cref="TenantMiddleware"/>), a Blazor circuit keeps the one of the request that opened it
/// (<see cref="TenantCircuitHandler"/>), and work outside a request (startup, pruning) names it with
/// <see cref="Use"/>. It flows with the async context, so the database, the settings and the options
/// resolved underneath all belong to that tenant. Without one, it is the default tenant.
/// </summary>
public static class TenantContext
{
    private static readonly AsyncLocal<Tenant?> Ambient = new();

    public static Tenant Current => Ambient.Value ?? Tenant.Default;

    /// <summary>Makes <paramref name="tenant"/> the current one until the result is disposed.</summary>
    public static Scope Use(Tenant tenant)
    {
        var previous = Ambient.Value;
        Ambient.Value = tenant;
        return new Scope(previous);
    }

    public readonly struct Scope(Tenant? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>A dependency injection scope whose services (database, managers) belong to one tenant.</summary>
public sealed class TenantServiceScope : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;
    private readonly TenantContext.Scope _tenant;

    public TenantServiceScope(IServiceProvider services, Tenant tenant)
    {
        _tenant = TenantContext.Use(tenant);
        _scope = services.CreateAsyncScope();
    }

    public IServiceProvider ServiceProvider => _scope.ServiceProvider;

    // Not an async method: the previous tenant must come back in the caller's async context.
    public ValueTask DisposeAsync()
    {
        var disposal = _scope.DisposeAsync();
        _tenant.Dispose();
        return disposal;
    }
}

public static class TenantServiceProviderExtensions
{
    /// <summary>A scope for work in <paramref name="tenant"/>; dispose it in the method that created it.</summary>
    public static TenantServiceScope CreateTenantScope(this IServiceProvider services, Tenant tenant) => new(services, tenant);
}
