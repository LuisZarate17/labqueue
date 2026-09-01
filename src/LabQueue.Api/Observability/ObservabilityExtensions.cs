using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LabQueue.Api.Observability;

public static class ObservabilityExtensions
{
    /// <summary>Identical in every environment. What distinguishes them is the attribute below.</summary>
    public const string ServiceName = "labqueue-api";

    private const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string EnvironmentVariable = "OTEL_DEPLOYMENT_ENVIRONMENT";

    /// <summary>
    /// Wires OpenTelemetry when — and only when — an OTLP endpoint is configured.
    ///
    /// The absent-endpoint case is not a detail. Registering the exporter unconditionally
    /// points it at localhost:4317 by default, where it retries and stalls shutdown; the
    /// test suite builds a host per test class, so that cost lands once per test and CI pays
    /// for telemetry nobody collects. Local runs without a .env behave the same way.
    /// </summary>
    public static WebApplicationBuilder AddLabQueueObservability(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<LabQueueMetrics>();

        // Read from the environment rather than IConfiguration on purpose: the OpenTelemetry
        // SDK reads its endpoint, headers and protocol straight from environment variables,
        // so a value that only exists in appsettings.json would satisfy the check here and
        // then fail to configure the exporter.
        var endpoint = Environment.GetEnvironmentVariable(EndpointVariable);

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return builder;
        }

        var deploymentEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);

        if (string.IsNullOrWhiteSpace(deploymentEnvironment))
        {
            throw new InvalidOperationException(
                $"{EndpointVariable} is set, so {EnvironmentVariable} must be set too - "
                + "'local' or 'hosted'. Both environments export to one Grafana stack and every "
                + "panel filters on this attribute, so telemetry without it is invisible under "
                + "either filter rather than merely mislabelled. See .env.example.");
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: ServiceName)
                .AddAttributes([
                    new KeyValuePair<string, object>("deployment.environment", deploymentEnvironment)
                ]))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddNpgsqlInstrumentation()
                .AddMeter(LabQueueMetrics.MeterName)
                .AddOtlpExporter())
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    // The container healthcheck polls /health every 30 seconds and Render polls
                    // it too. Those are worth counting and not worth tracing. Metrics keep the
                    // route so a panel can include or exclude it; traces just drop it.
                    //
                    // The docs page and its document are dropped for the same reason: a reviewer
                    // clicking around /docs would otherwise spend a 50GB free trace quota on
                    // page loads. The API calls they make from it are still traced.
                    options.Filter = context =>
                        context.Request.Path != "/health"
                        && !context.Request.Path.StartsWithSegments("/docs")
                        && !context.Request.Path.StartsWithSegments("/openapi"))
                .AddNpgsql()
                .AddOtlpExporter());

        return builder;
    }
}
