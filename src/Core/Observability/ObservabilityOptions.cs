namespace Core.Observability;

public sealed class ObservabilityOptions
{
    public const string ConfigurationSection = "Observability";

    /// <summary>"Otlp" (production — export to a real OpenTelemetry collector) or "Console" (local dev — print
    /// traces/metrics to stdout, so instrumentation is visibly exercised without needing a collector running).</summary>
    public string Exporter { get; set; } = "Otlp";

    public string OtlpEndpoint { get; set; } = "http://localhost:4317";
}
