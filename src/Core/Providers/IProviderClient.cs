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

    /// <summary>
    /// Streams a chat completion as OpenAI-shaped SSE (<c>data: {...}\n\n</c> frames, terminated by
    /// <c>data: [DONE]\n\n</c>) written directly to <paramref name="writer"/>'s body as it arrives — never
    /// buffered in full. Providers that don't natively speak OpenAI's streaming format translate frame-by-frame
    /// as they go (see <c>Providers.Anthropic.AnthropicStreamTranslator</c>).
    /// <para>
    /// If the provider rejects the request before any streaming data was sent (e.g. bad credentials — real
    /// providers return a normal non-streaming JSON error for this, not an SSE event), implementations switch
    /// <paramref name="writer"/> to the real status code and <c>application/json</c> instead of pretending it
    /// was a successful stream. This only works because it happens before the first write to the body.
    /// </para>
    /// </summary>
    /// <returns>Token usage once the stream completes successfully — null on error, or if the provider didn't report it.</returns>
    Task<StreamUsage?> StreamChatCompletionAsync(JsonObject request, string apiKey, IStreamResponseWriter writer, CancellationToken cancellationToken);
}
