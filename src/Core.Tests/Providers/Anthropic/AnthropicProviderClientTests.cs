using System.Net;
using System.Text.Json.Nodes;
using Core.Providers.Anthropic;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Tests.Providers.Anthropic;

public class AnthropicProviderClientTests
{
    private static AnthropicProviderClient CreateClient(FakeHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/") };
        return new AnthropicProviderClient(httpClient, Options.Create(new AnthropicProviderOptions()));
    }

    [Fact]
    public async Task Sends_translated_request_with_api_key_and_version_headers()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"id":"msg_1","content":[]}""");
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "claude-3-5-sonnet-20241022", ["messages"] = new JsonArray() };

        await client.CreateChatCompletionAsync(request, "sk-ant-test", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("sk-ant-test", handler.LastRequest.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());
        Assert.Contains("\"max_tokens\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task Returns_the_response_translated_into_openai_shape()
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.OK,
            """{"id":"msg_1","model":"claude-3-5-sonnet-20241022","content":[{"type":"text","text":"Hi!"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":2}}""");
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "claude-3-5-sonnet-20241022", ["messages"] = new JsonArray() };

        var response = await client.CreateChatCompletionAsync(request, "sk-ant-test", CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("chat.completion", response.Body?["object"]?.GetValue<string>());
        Assert.Equal("Hi!", response.Body?["choices"]?[0]?["message"]?["content"]?.GetValue<string>());
    }
}
