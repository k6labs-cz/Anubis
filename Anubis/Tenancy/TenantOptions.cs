using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

namespace k6labs.Anubis.Tenancy;

/// <summary>
/// Options that differ between tenants. <see cref="IOptionsMonitor{TOptions}"/> keeps one instance per
/// name; this cache keeps one per tenant and name, so whoever asks for options (the application, ASP.NET
/// Core Identity, the cookie handler, OpenIddict) gets those of the current tenant, built by the usual
/// configure actions while that tenant is the current one. Everything is forgotten when any tenant's
/// configuration changes, so settings saved in the admin pages apply to the next request.
/// </summary>
public sealed class TenantOptionsCache<TOptions> : IOptionsMonitorCache<TOptions>, IDisposable
    where TOptions : class
{
    private readonly ConcurrentDictionary<(string Tenant, string Name), Lazy<TOptions>> _cache = new();
    private readonly IDisposable _subscription;

    public TenantOptionsCache(TenantConfigurations configurations)
    {
        _subscription = configurations.OnChange(Clear);
    }

    public TOptions GetOrAdd(string? name, Func<TOptions> createOptions)
    {
        var key = Key(name);
        var lazy = _cache.GetOrAdd(key, _ => new Lazy<TOptions>(createOptions));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // Options that failed to build (e.g. invalid settings) are built again next time.
            _cache.TryRemove(new KeyValuePair<(string, string), Lazy<TOptions>>(key, lazy));
            throw;
        }
    }

    public bool TryAdd(string? name, TOptions options) => _cache.TryAdd(Key(name), new Lazy<TOptions>(() => options));

    public bool TryRemove(string? name) => _cache.TryRemove(Key(name), out _);

    public void Clear() => _cache.Clear();

    public void Dispose() => _subscription.Dispose();

    private static (string Tenant, string Name) Key(string? name) => (TenantContext.Current.Id, name ?? Microsoft.Extensions.Options.Options.DefaultName);
}

/// <summary><see cref="IOptions{TOptions}"/> of the current tenant (the default implementation keeps the first value for good).</summary>
public sealed class CurrentTenantOptions<TOptions>(IOptionsMonitor<TOptions> monitor) : IOptions<TOptions>
    where TOptions : class
{
    public TOptions Value => monitor.CurrentValue;
}

public static class TenantOptions
{
    /// <summary>
    /// Makes <typeparamref name="TOptions"/> per tenant for every way of asking for them (IOptions,
    /// IOptionsSnapshot, IOptionsMonitor), and with <paramref name="section"/> binds them from that
    /// section of the tenant's configuration.
    /// </summary>
    public static OptionsBuilder<TOptions> AddTenantOptions<TOptions>(this IServiceCollection services, string? section = null)
        where TOptions : class
    {
        services.AddSingleton<IOptionsMonitorCache<TOptions>, TenantOptionsCache<TOptions>>();
        services.AddSingleton<IOptions<TOptions>, CurrentTenantOptions<TOptions>>();

        var builder = services.AddOptions<TOptions>();
        return section is null
            ? builder
            : builder.Configure<TenantConfigurations>((options, configurations) => configurations.Current.GetSection(section).Bind(options));
    }
}
