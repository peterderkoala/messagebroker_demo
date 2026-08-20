using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NotificationPlatform.BuildingBlocks.Caching;

/// <summary>
/// The shared cache-aside setup (<c>CONTEXT.md</c> "Cache Entry"): every
/// service that caches reads goes through the same Redis wiring and the same
/// get-or-populate helper, so the pattern only has to be written once.
/// </summary>
public static class CacheExtensions
{
    /// <summary>
    /// Wires <see cref="IDistributedCache"/> against the shared Redis
    /// container, prefixing every key this service writes with
    /// <paramref name="serviceName"/>.
    /// </summary>
    /// <remarks>
    /// ADR-0002: a Cache Entry is owned by the service that writes it and is
    /// keyed within that service - <see cref="Microsoft.Extensions.Caching.StackExchangeRedis.RedisCacheOptions.InstanceName"/>
    /// enforces that automatically even though every service shares one
    /// Redis container, the same way database-per-service does for Postgres.
    /// </remarks>
    public static IHostApplicationBuilder AddPlatformCache(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var redisHost = builder.Configuration["REDIS_HOST"] ?? "redis";
        var redisPort = builder.Configuration["REDIS_PORT"] ?? "6379";

        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = $"{redisHost}:{redisPort}";
            options.InstanceName = serviceName;
        });

        return builder;
    }

    /// <summary>
    /// The cache-aside pattern every read path should use: try Redis first,
    /// and on a miss call <paramref name="loadAsync"/> (the source of
    /// truth), populating the Cache Entry with <paramref name="ttl"/> before
    /// returning.
    /// </summary>
    /// <remarks>
    /// A miss at the source is never cached - an as-yet-invisible row (e.g.
    /// a write not yet committed) is retried on the next read instead of
    /// being parked as a false "not found" until the TTL expires.
    /// </remarks>
    public static async Task<T?> GetOrCreateAsync<T>(
        this IDistributedCache cache,
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T?>> loadAsync,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(loadAsync);

        var cached = await cache.GetStringAsync(key, cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<T>(cached);
        }

        var value = await loadAsync(cancellationToken);
        if (value is not null)
        {
            await cache.SetStringAsync(
                key,
                JsonSerializer.Serialize(value),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                cancellationToken);
        }

        return value;
    }
}
