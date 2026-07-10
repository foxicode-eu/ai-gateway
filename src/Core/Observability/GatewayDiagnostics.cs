using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Core.Observability;

/// <summary>
/// Shared <see cref="ActivitySource"/>/<see cref="Meter"/> for gateway-specific instrumentation (as opposed to
/// the generic ASP.NET Core/HttpClient instrumentation <see cref="ServiceCollectionExtensions.AddGatewayObservability"/>
/// wires up). Only <c>Api</c> emits these today — <c>Management</c> doesn't proxy chat requests.
/// <para>
/// Metric tags include <c>tenant_id</c>, per ARCHITECTURE.md's "metrics tracked per tenant" requirement. That's
/// a deliberate, known cardinality trade-off (one time series per tenant per provider/model/status combination)
/// — acceptable at this stage, worth revisiting if tenant count grows large enough for it to matter.
/// </para>
/// <para>
/// Payload privacy: nothing here ever carries prompt/completion content — only metadata (tenant/provider/model
/// identifiers, token counts, status codes, latency). See ARCHITECTURE.md's "Payload privacy" rule.
/// </para>
/// </summary>
public static class GatewayDiagnostics
{
    public const string SourceName = "AiGateway";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> RequestCount =
        Meter.CreateCounter<long>("gateway.requests", description: "Chat completion requests handled.");

    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("gateway.request.duration", unit: "ms", description: "Chat completion request latency.");

    /// <summary>Tag <c>token_type</c> distinguishes prompt vs. completion tokens.</summary>
    public static readonly Counter<long> TokensUsed =
        Meter.CreateCounter<long>("gateway.tokens", description: "Tokens consumed per request.");
}
