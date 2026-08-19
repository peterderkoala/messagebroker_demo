using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;

namespace NotificationPlatform.BuildingBlocks.HealthChecks;

/// <summary>
/// The health endpoints every service exposes, and that docker-compose gates
/// startup ordering on.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// Marks a health check as safe to run on the liveness endpoint. Anything
    /// without this tag is treated as a readiness concern.
    /// </summary>
    public const string LiveTag = "live";

    /// <summary>
    /// Maps <c>/health/live</c> and <c>/health/ready</c>.
    /// </summary>
    /// <remarks>
    /// The split matters for compose: <c>/health/ready</c> reports on
    /// dependencies (Postgres, RabbitMQ, Redis), so it is what a
    /// <c>depends_on: condition: service_healthy</c> gate should watch.
    /// <c>/health/live</c> answers only "is this process still working", and
    /// stays green while a dependency is down - restarting the process would
    /// not fix that, so nothing should kill it on that basis.
    /// </remarks>
    public static IEndpointRouteBuilder MapPlatformHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LiveTag),
        });

        endpoints.MapHealthChecks("/health/ready");

        return endpoints;
    }
}
