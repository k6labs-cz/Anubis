using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace k6labs.Anubis.Data;

/// <summary>
/// Periodically removes expired tokens and account history older than a year from the
/// SQLite database of every tenant so it does not grow unbounded (replaces the Quartz.NET pruning job
/// recommended for large deployments).
/// </summary>
public sealed class TokenPruningService : BackgroundService
{
    private static readonly TimeSpan PruningInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(14);
    public static readonly TimeSpan HistoryRetentionPeriod = TimeSpan.FromDays(365);

    private readonly IServiceProvider _services;
    private readonly ILogger<TokenPruningService> _logger;

    public TokenPruningService(IServiceProvider services, ILogger<TokenPruningService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var tenant in _services.GetRequiredService<TenantCatalog>().All.ToList())
                {
                    try
                    {
                        await PruneAsync(tenant, stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // One tenant's failure (e.g. it was deleted meanwhile) does not stop the others.
                        _logger.LogError(exception, "Token pruning failed for the tenant {Tenant}.", tenant.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(PruningInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PruneAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateTenantScope(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AnubisDbContext>();

        var cutoff = DateTimeOffset.UtcNow - RetentionPeriod;

        // SQLite cannot translate DateTimeOffset comparisons, so the expiration date
        // is filtered client-side after loading only the id/expiration columns.
        var candidates = await context.Set<Token>()
            .Where(token => token.ExpirationDate != null)
            .Select(token => new { token.Id, token.ExpirationDate })
            .ToListAsync(cancellationToken);

        var expiredIds = candidates
            .Where(token => token.ExpirationDate!.Value < cutoff)
            .Select(token => token.Id)
            .ToList();

        if (expiredIds.Count > 0)
        {
            var expired = await context.Set<Token>()
                .Where(token => expiredIds.Contains(token.Id))
                .ToListAsync(cancellationToken);

            context.RemoveRange(expired);
            await context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Pruned {Count} expired tokens older than {Cutoff:u} in the tenant {Tenant}.", expired.Count, cutoff, tenant.Id);
        }

        var historyCutoff = DateTime.UtcNow - HistoryRetentionPeriod;
        var pruned = await context.UserEvents.Where(e => e.OccurredAtUtc < historyCutoff).ExecuteDeleteAsync(cancellationToken);
        if (pruned > 0)
        {
            _logger.LogInformation("Pruned {Count} account history entries older than {Cutoff:u} in the tenant {Tenant}.", pruned, historyCutoff, tenant.Id);
        }
    }
}
