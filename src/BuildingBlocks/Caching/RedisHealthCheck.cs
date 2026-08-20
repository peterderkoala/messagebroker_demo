using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NotificationPlatform.BuildingBlocks.Caching;

/// <summary>
/// Reports on the same Redis connection <see cref="CacheExtensions.AddPlatformCache"/>
/// wires up, via a round-trip on a small probe key.
/// </summary>
/// <remarks>
/// A failed round-trip reports <see cref="HealthStatus.Degraded"/>, not
/// <see cref="HealthStatus.Unhealthy"/> - the same convention
/// <c>HealthCheckExtensions</c> already uses for MassTransit's bus check.
/// A Redis outage never fails a request (<see cref="CacheExtensions"/>
/// degrades every call to the source of truth), so this check exists purely
/// for visibility on <c>/health/ready</c>, not to gate readiness on Redis
/// being reachable. The connection's own <c>ConnectTimeout</c>/<c>SyncTimeout</c>/
/// <see cref="StackExchange.Redis.BacklogPolicy.FailFast"/> settings (set in
/// <see cref="CacheExtensions.AddPlatformCache"/>) already bound how long a
/// failed round-trip takes, so this check adds no timeout of its own.
/// </remarks>
public sealed class RedisHealthCheck(IDistributedCache cache) : IHealthCheck
{
    private const string ProbeKey = "health-check-probe";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await cache.SetStringAsync(
                ProbeKey,
                DateTimeOffset.UtcNow.ToString("O"),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) },
                cancellationToken);

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Redis is unreachable; reads are falling back to the source of truth.", ex);
        }
    }
}

public static class RedisHealthCheckExtensions
{
    /// <summary>
    /// Registers the Redis health check against the connection
    /// <see cref="CacheExtensions.AddPlatformCache"/> configured.
    /// </summary>
    public static IHealthChecksBuilder AddPlatformCacheHealthCheck(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCheck<RedisHealthCheck>("redis");
    }
}
