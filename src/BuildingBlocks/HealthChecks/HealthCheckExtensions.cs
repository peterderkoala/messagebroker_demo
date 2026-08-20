using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            // The default response body is just "Healthy"/"Unhealthy" - naming
            // which dependency failed (Postgres vs. RabbitMQ vs. Redis) is
            // what actually makes /health/ready useful to look at directly.
            ResponseWriter = WriteReadinessReportAsync,

            // MassTransit's bus health check reports a disconnected broker as
            // Degraded, not Unhealthy (by design - it keeps retrying rather
            // than asking to be killed for a transient network blip). The
            // ASP.NET Core default maps Degraded to 200, same as Healthy, so
            // without this override a real RabbitMQ outage would never flip
            // the HTTP status code and compose's healthcheck would stay green.
            ResultStatusCodes =
            {
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
            },
        });

        return endpoints;
    }

    private static Task WriteReadinessReportAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
