using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Core.Providers.Anthropic;

public sealed class AnthropicProviderClient(HttpClient httpClient, IOptions<AnthropicProviderOptions> options) : IProviderClient
{
    private readonly AnthropicProviderOptions _options = options.Value;

    public string ProviderName => "anthropic";

    public async Task<ProviderResponse> CreateChatCompletionAsync(JsonObject request, string apiKey, CancellationToken cancellationToken)
    {
        var anthropicRequest = AnthropicChatTranslator.ToAnthropicRequest(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(anthropicRequest.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add("x-api-key", apiKey);
        httpRequest.Headers.Add("anthropic-version", _options.ApiVersion);

        using var httpResponse = await httpClient.SendAsync(httpRequest, cancellationToken);
        var responseText = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        var anthropicBody = string.IsNullOrWhiteSpace(responseText)
            ? null
            : JsonNode.Parse(responseText) as JsonObject;

        var openAiBody = anthropicBody is null ? null : AnthropicChatTranslator.ToOpenAiResponse(anthropicBody);

        return new ProviderResponse((int)httpResponse.StatusCode, openAiBody);
    }

    public async Task<StreamUsage?> StreamChatCompletionAsync(
        JsonObject request, string apiKey, IStreamResponseWriter writer, CancellationToken cancellationToken)
    {
        var anthropicRequest = AnthropicChatTranslator.ToAnthropicRequest(request);
        anthropicRequest["stream"] = true;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(anthropicRequest.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add("x-api-key", apiKey);
        httpRequest.Headers.Add("anthropic-version", _options.ApiVersion);

        using var httpResponse = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // Anthropic rejects a bad request/credential with a normal non-streaming JSON error before any SSE
        // data is sent, even when stream:true was requested — mirror that instead of pretending it was a stream.
        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorText = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            var openAiError = string.IsNullOrWhiteSpace(errorText) || JsonNode.Parse(errorText) is not JsonObject errorBody
                ? new JsonObject { ["error"] = new JsonObject { ["message"] = "Unknown provider error." } }
                : AnthropicChatTranslator.ToOpenAiResponse(errorBody);

            writer.SetStatusCode((int)httpResponse.StatusCode);
            writer.SetContentType("application/json");
            await writer.Body.WriteAsync(Encoding.UTF8.GetBytes(openAiError.ToJsonString()), cancellationToken);
            return null;
        }

        await using var upstream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(upstream);

        var translator = new AnthropicStreamTranslator();
        string? currentEvent = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal) || currentEvent is null)
            {
                continue;
            }

            var eventType = currentEvent;
            currentEvent = null;

            if (JsonNode.Parse(line["data:".Length..].Trim()) is not JsonObject data)
            {
                continue;
            }

            var chunk = translator.ProcessEvent(eventType, data);
            if (chunk is not null)
            {
                await WriteSseDataAsync(writer.Body, chunk.ToJsonString(), cancellationToken);
            }

            if (translator.IsDone)
            {
                await WriteSseDataAsync(writer.Body, "[DONE]", cancellationToken);
                break;
            }
        }

        return translator.FinalUsage;
    }

    private static async Task WriteSseDataAsync(Stream destination, string payload, CancellationToken cancellationToken)
    {
        await destination.WriteAsync(Encoding.UTF8.GetBytes($"data: {payload}\n\n"), cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }
}
