using System.Text.Json.Nodes;

namespace Core.Providers;

/// <summary>A provider's raw HTTP response, passed through to the gateway caller largely as-is.</summary>
public sealed record ProviderResponse(int StatusCode, JsonObject? Body);
