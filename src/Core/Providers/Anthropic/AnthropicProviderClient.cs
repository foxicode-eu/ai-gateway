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
}
