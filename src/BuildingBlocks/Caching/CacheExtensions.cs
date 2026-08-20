using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using StackExchange.Redis;

namespace NotificationPlatform.BuildingBlocks.Caching;

/// <summary>
/// The shared cache-aside setup (<c>CONTEXT.md</c> "Cache Entry"): every
/// service that caches reads goes through the same Redis wiring and the same
/// get-or-populate helper, so the pattern only has to be written once.
/// </summary>
public static class CacheExtensions
{
    /// <summary>
    /// Guards every Redis call this class makes (#17,
    /// <see cref="Resilience.ResilienceDefaults"/>): one fast retry for a
    /// transient blip, then a tight per-attempt timeout, then give up. A
    /// fault that survives this is caught at each call site below and
    /// treated as "Redis unavailable right now" - never as a reason to fail
    /// the caller, since Postgres is the actual source of truth.
    /// </summary>
    private static readonly ResiliencePipeline CachePipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<RedisException>(),
            MaxRetryAttempts = 1,
            Delay = TimeSpan.FromMilliseconds(25),
        })
        .AddTimeout(TimeSpan.FromMilliseconds(200))
        .Build();

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
    /// <para>
    /// The connection settings are deliberately not left at their defaults
    /// (#17), because <see cref="CachePipeline"/>'s own retry/timeout only
    /// bounds anything if StackExchange.Redis fails fast underneath it:
    /// </para>
    /// <list type="bullet">
    /// <item><c>ConnectTimeout</c>/<c>SyncTimeout</c>/<c>AsyncTimeout</c> are
    /// cut to 300ms from their (several-second) defaults.</item>
    /// <item><c>BacklogPolicy.FailFast</c> is required in addition to the
    /// timeouts above, not instead of them - by default StackExchange.Redis
    /// queues a command in a backlog to wait for a connection rather than
    /// failing it immediately, and that backlog wait was observed adding
    /// several seconds on top of the configured timeouts (a Redis outage
    /// took 2-4s per call with the timeouts alone, 12-23s at the library
    /// defaults; with <c>FailFast</c> added, under 350ms). Polly's own
    /// timeout strategy could not shorten this either way: Polly v8's
    /// timeout is cooperative-cancellation only, so it cannot abort an
    /// operation that will not itself observe the token - the bound has to
    /// come from the client actually failing fast, not from wrapping a slow
    /// call in a faster-sounding policy.</item>
    /// <item><c>ConnectRetry</c> is 0, for the same reason
    /// <see cref="ResilienceDefaults"/> keeps Polly out of a MassTransit
    /// consumer: retrying at two independent layers for the same fault just
    /// multiplies the same delay twice.</item>
    /// </list>
    /// </remarks>
    public static IHostApplicationBuilder AddPlatformCache(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var redisHost = builder.Configuration["REDIS_HOST"] ?? "redis";
        var redisPort = int.Parse(builder.Configuration["REDIS_PORT"] ?? "6379");

        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.ConfigurationOptions = new ConfigurationOptions
            {
                EndPoints = { { redisHost, redisPort } },
                ConnectTimeout = 300,
                ConnectRetry = 0,
                SyncTimeout = 300,
                AsyncTimeout = 300,
                AbortOnConnectFail = false,
                BacklogPolicy = BacklogPolicy.FailFast,
            };
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
    /// <para>
    /// A Redis fault is treated exactly like a cache miss (#17): the read
    /// falls through to <paramref name="loadAsync"/> regardless, and
    /// populating the Cache Entry afterward is itself best-effort - a
    /// second Redis fault there is logged and swallowed rather than failing
    /// a read that already succeeded from the source of truth.
    /// </para>
    /// </remarks>
    public static async Task<T?> GetOrCreateAsync<T>(
        this IDistributedCache cache,
        ILogger logger,
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T?>> loadAsync,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(loadAsync);

        var cached = await TryGetStringAsync(cache, logger, key, cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<T>(cached);
        }

        var value = await loadAsync(cancellationToken);
        if (value is not null)
        {
            await TrySetStringAsync(cache, logger, key, JsonSerializer.Serialize(value), ttl, cancellationToken);
        }

        return value;
    }

    /// <summary>
    /// Evicts a Cache Entry so the next read repopulates it from the source
    /// of truth. Every write path that could make a cached read stale
    /// should call this before returning.
    /// </summary>
    /// <remarks>
    /// Best-effort, like the rest of this class (#17): a Redis fault here
    /// must never fail the write that is invalidating the entry - it will
    /// still expire via its TTL, just not immediately.
    /// </remarks>
    public static async Task InvalidateAsync(
        this IDistributedCache cache, ILogger logger, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            await CachePipeline.ExecuteAsync(
                async ct => await cache.RemoveAsync(key, ct),
                cancellationToken);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutRejectedException)
        {
            logger.LogWarning(ex, "Cache Entry invalidation failed for {CacheKey}; it will still expire via its TTL.", key);
        }
    }

    private static async Task<string?> TryGetStringAsync(
        IDistributedCache cache, ILogger logger, string key, CancellationToken cancellationToken)
    {
        try
        {
            return await CachePipeline.ExecuteAsync(
                async ct => await cache.GetStringAsync(key, ct),
                cancellationToken);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutRejectedException)
        {
            logger.LogWarning(ex, "Cache Entry read failed for {CacheKey}; falling back to the source of truth.", key);
            return null;
        }
    }

    private static async Task TrySetStringAsync(
        IDistributedCache cache, ILogger logger, string key, string value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        try
        {
            await CachePipeline.ExecuteAsync(
                async ct => await cache.SetStringAsync(
                    key,
                    value,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                    ct),
                cancellationToken);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutRejectedException)
        {
            logger.LogWarning(ex, "Cache Entry write failed for {CacheKey}; the next read will retry it.", key);
        }
    }
}
