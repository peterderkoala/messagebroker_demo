namespace NotificationPlatform.BuildingBlocks.Resilience;

/// <summary>
/// The shared resilience surface: where a Polly pipeline sits, and where it
/// deliberately does not (#17).
/// </summary>
/// <remarks>
/// One boundary got a policy: a Redis Cache Entry read or write
/// (<see cref="Caching.CacheExtensions"/>). An outage there must degrade to
/// Postgres, not fail the request - Redis is only ever an accelerator in
/// front of a database that is already the source of truth, so the pipeline
/// there is one fast retry, a tight timeout, then give up and let the
/// caller fall through.
/// <para>
/// One boundary deliberately got none: the simulated delivery call inside a
/// Channel Service's consumer. It already runs inside MassTransit's own
/// <c>UseMessageRetry</c> plus automatic dead-letter queue (#2's findings) -
/// wrapping it in a second Polly retry would retry the same failure at two
/// levels with compounding delays, exactly what this ticket exists to
/// avoid. If a future ticket gives that call something that can genuinely
/// fail (a real provider call), MassTransit's own retry stays the only
/// layer around it; Polly does not reach inside a consumer.
/// </para>
/// <para>
/// <see cref="HttpPipelineName"/> is reserved for outbound HTTP between
/// services - nothing makes one of those calls yet, so nothing consumes it.
/// </para>
/// <para>
/// The one resilience policy that predates this ticket is the startup
/// migration retry in <see cref="Persistence.DatabaseMigrationExtensions"/>,
/// required by ADR-0002 before any service can start - out of scope here,
/// carried over unchanged.
/// </para>
/// </remarks>
public static class ResilienceDefaults
{
    /// <summary>
    /// Name of the shared HTTP resilience pipeline, reserved for outbound
    /// calls between services. Nothing makes one of those calls yet.
    /// </summary>
    public const string HttpPipelineName = "notification-platform-http";
}
