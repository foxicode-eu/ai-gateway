using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Core.Observability;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wires up OpenTelemetry tracing + metrics: ASP.NET Core and HttpClient auto-instrumentation, plus
    /// <see cref="GatewayDiagnostics"/>'s custom gateway metrics. Exporter is swappable via
    /// <c>Observability:Exporter</c> ("Otlp" for production — a real collector, or "Console" for local dev, so
    /// instrumentation is visibly exercised without standing up a collector).
    /// </summary>
    public static IServiceCollection AddGatewayObservability(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        var options = new ObservabilityOptions();
        configuration.GetSection(ObservabilityOptions.ConfigurationSection).Bind(options);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation();
                tracing.AddHttpClientInstrumentation();
                tracing.AddSource(GatewayDiagnostics.SourceName);
                ConfigureExporter(tracing, options);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddHttpClientInstrumentation();
                metrics.AddMeter(GatewayDiagnostics.SourceName);
                ConfigureExporter(metrics, options);
            });

        return services;
    }

    private static void ConfigureExporter(TracerProviderBuilder builder, ObservabilityOptions options)
    {
        switch (options.Exporter)
        {
            case "Otlp":
                builder.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(options.OtlpEndpoint));
                break;
            case "Console":
                builder.AddConsoleExporter();
                break;
            default:
                throw new InvalidOperationException(
                    $"Missing or unrecognized '{ObservabilityOptions.ConfigurationSection}:Exporter' configuration (expected \"Otlp\" or \"Console\", got \"{options.Exporter}\").");
        }
    }

    private static void ConfigureExporter(MeterProviderBuilder builder, ObservabilityOptions options)
    {
        switch (options.Exporter)
        {
            case "Otlp":
                builder.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(options.OtlpEndpoint));
                break;
            case "Console":
                builder.AddConsoleExporter();
                break;
            default:
                throw new InvalidOperationException(
                    $"Missing or unrecognized '{ObservabilityOptions.ConfigurationSection}:Exporter' configuration (expected \"Otlp\" or \"Console\", got \"{options.Exporter}\").");
        }
    }
}
