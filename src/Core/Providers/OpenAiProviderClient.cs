using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Core.Providers;

public sealed class OpenAiProviderClient : IProviderClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiProviderOptions _options;

    public OpenAiProviderClient(HttpClient httpClient, IOptions<OpenAiProviderOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public string ProviderName => "openai";

    public async Task<ProviderResponse> CreateChatCompletionAsync(JsonObject request, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseText = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        var body = string.IsNullOrWhiteSpace(responseText)
            ? null
            : JsonNode.Parse(responseText) as JsonObject;

        return new ProviderResponse((int)httpResponse.StatusCode, body);
    }
}
