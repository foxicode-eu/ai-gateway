using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Core.Providers;
using Xunit;

namespace Core.Tests.Providers;

public class OpenAiProviderClientTests
{
    private static OpenAiProviderClient CreateClient(FakeHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        return new OpenAiProviderClient(httpClient);
    }

    [Fact]
    public async Task Sends_request_body_and_bearer_token_to_openai_chat_completions_endpoint()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"id":"chatcmpl-123"}""");
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "gpt-4o-mini", ["messages"] = new JsonArray() };

        await client.CreateChatCompletionAsync(request, "sk-test-123", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "sk-test-123"), handler.LastRequest.Headers.Authorization);
        Assert.Equal("""{"model":"gpt-4o-mini","messages":[]}""", handler.LastRequestBody);
    }

    [Fact]
    public async Task Returns_provider_status_code_and_parsed_body()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"id":"chatcmpl-123","object":"chat.completion"}""");
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "gpt-4o-mini" };

        var response = await client.CreateChatCompletionAsync(request, "sk-test-123", CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("chatcmpl-123", response.Body?["id"]?.GetValue<string>());
    }

    [Fact]
    public async Task Propagates_error_status_codes_from_the_provider()
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API key"}}""");
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "gpt-4o-mini" };

        var response = await client.CreateChatCompletionAsync(request, "sk-test-123", CancellationToken.None);

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("Invalid API key", response.Body?["error"]?["message"]?.GetValue<string>());
    }
}
