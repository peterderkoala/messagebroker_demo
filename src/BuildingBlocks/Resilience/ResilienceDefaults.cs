namespace NotificationPlatform.BuildingBlocks.Resilience;

/// <summary>
/// The shared resilience surface. Deliberately thin for now.
/// </summary>
/// <remarks>
/// Only the constants both ends of a policy have to agree on live here.
/// The policies themselves are not written yet: which calls get wrapped, and
/// where the boundary sits between a Polly retry and MassTransit's own
/// <c>UseMessageRetry</c>, is what issue #17 decides. Guessing at them now
/// would mean two retry layers multiplying each other's delays inside a
/// consumer, which is the specific mistake that ticket exists to avoid.
/// <para>
/// The one resilience policy that already exists is the startup migration
/// retry in <see cref="Persistence.DatabaseMigrationExtensions"/>, because
/// ADR-0002 requires it before any service can start.
/// </para>
/// </remarks>
public static class ResilienceDefaults
{
    /// <summary>
    /// Name of the shared HTTP resilience pipeline, so services register and
    /// resolve it by the same key once #17 defines what it does.
    /// </summary>
    public const string HttpPipelineName = "notification-platform-http";
}
