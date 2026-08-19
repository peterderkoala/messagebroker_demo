using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NotificationPlatform.BuildingBlocks.Observability;

/// <summary>
/// One OpenTelemetry setup, shared by every service, so traces from the
/// Notification API and the Channel Services land in the Aspire Dashboard
/// looking like one system rather than four.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// Wires traces, metrics, and logs to the OTLP endpoint named by the
    /// standard <c>OTEL_EXPORTER_OTLP_*</c> environment variables, which
    /// docker-compose supplies. Nothing here reads configuration directly -
    /// the exporter picks the variables up itself.
    /// </summary>
    /// <param name="serviceName">
    /// How this service identifies itself in the dashboard. Also set
    /// <c>OTEL_SERVICE_NAME</c> in compose so the two agree.
    /// </param>
    public static IHostApplicationBuilder AddPlatformObservability(
        this IHostApplicationBuilder builder,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                // MassTransit emits its own activities under this source, which
                // is what makes a publish and its consume show up as one trace.
                .AddSource("MassTransit"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .UseOtlpExporter();

        return builder;
    }
}
