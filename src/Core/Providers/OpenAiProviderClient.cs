using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Core.Providers;

public sealed class OpenAiProviderClient(HttpClient httpClient) : IProviderClient
{
    public string ProviderName => "openai";

    public async Task<ProviderResponse> CreateChatCompletionAsync(JsonObject request, string apiKey, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var httpResponse = await httpClient.SendAsync(httpRequest, cancellationToken);
        var responseText = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        var body = string.IsNullOrWhiteSpace(responseText)
            ? null
            : JsonNode.Parse(responseText) as JsonObject;

        return new ProviderResponse((int)httpResponse.StatusCode, body);
    }

    public async Task<StreamUsage?> StreamChatCompletionAsync(
        JsonObject request, string apiKey, IStreamResponseWriter writer, CancellationToken cancellationToken)
    {
        // The gateway's public streaming contract is already OpenAI's own SSE shape, so this is a byte
        // pass-through — no per-chunk translation needed. We only peek at each line to spot the final usage
        // chunk (requested via stream_options.include_usage) and to know when [DONE] has been forwarded.
        request["stream_options"] = new JsonObject { ["include_usage"] = true };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var httpResponse = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // OpenAI rejects a bad request/credential with a normal non-streaming JSON error before any SSE data
        // is sent, even when stream:true was requested — mirror that instead of pretending it was a stream.
        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            writer.SetStatusCode((int)httpResponse.StatusCode);
            writer.SetContentType("application/json");
            await writer.Body.WriteAsync(Encoding.UTF8.GetBytes(errorBody), cancellationToken);
            return null;
        }

        await using var upstream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(upstream);

        StreamUsage? usage = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            await writer.Body.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellationToken);

            if (line.Length == 0)
            {
                await writer.Body.FlushAsync(cancellationToken);
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                continue;
            }

            if (JsonNode.Parse(payload) is JsonObject chunk && chunk["usage"] is JsonObject usageNode)
            {
                usage = new StreamUsage(
                    usageNode["prompt_tokens"]?.GetValue<int>() ?? 0,
                    usageNode["completion_tokens"]?.GetValue<int>() ?? 0);
            }
        }

        return usage;
    }
}
