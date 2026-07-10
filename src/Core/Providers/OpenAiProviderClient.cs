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
}
