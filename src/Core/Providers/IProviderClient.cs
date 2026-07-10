using System.Text.Json.Nodes;

namespace Core.Providers;

/// <summary>
/// A backend inference provider (OpenAI, Anthropic, ...) the gateway can proxy chat completion requests to.
/// Request/response bodies are passed through as JSON rather than fully typed, since the public gateway surface
/// is already OpenAI-shaped — providers that don't natively speak that shape (e.g. Anthropic) translate at
/// their own client boundary rather than forcing a shared strongly-typed schema on every provider.
/// </summary>
public interface IProviderClient
{
    /// <summary>Matches the value tenants would configure to select this provider, e.g. "openai".</summary>
    string ProviderName { get; }

    /// <param name="apiKey">
    /// The tenant's own provider credential (BYOK), resolved per-request — never baked into the client at
    /// construction time, since one client instance is shared across all tenants.
    /// </param>
    Task<ProviderResponse> CreateChatCompletionAsync(JsonObject request, string apiKey, CancellationToken cancellationToken);
}
