using System.Net;
using System.Text.Json.Nodes;
using Core.Providers;
using Core.Providers.Anthropic;
using Core.Tests.Providers;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Tests.Providers.Anthropic;

public class AnthropicProviderClientStreamingTests
{
    private const string CannedSseBody =
        "event: message_start\n" +
        "data: {\"message\":{\"id\":\"msg_1\",\"model\":\"claude-3-5-sonnet-20241022\",\"usage\":{\"input_tokens\":8}}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\n" +
        "event: message_delta\n" +
        "data: {\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}\n\n" +
        "event: message_stop\n" +
        "data: {}\n\n";

    private static AnthropicProviderClient CreateClient(FakeHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/") };
        return new AnthropicProviderClient(httpClient, Options.Create(new AnthropicProviderOptions()));
    }

    [Fact]
    public async Task Translates_the_anthropic_stream_into_openai_shaped_sse_chunks()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, CannedSseBody);
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "claude-3-5-sonnet-20241022", ["messages"] = new JsonArray() };
        var writer = new FakeStreamResponseWriter();

        await client.StreamChatCompletionAsync(request, "sk-ant-test", writer, CancellationToken.None);

        var written = writer.BodyAsString();
        Assert.Contains("\"role\":\"assistant\"", written);
        Assert.Contains("\"content\":\"Hi\"", written);
        Assert.Contains("\"finish_reason\":\"stop\"", written);
        Assert.EndsWith("data: [DONE]\n\n", written);
        Assert.Equal(200, writer.StatusCode);
    }

    [Fact]
    public async Task Returns_combined_usage_once_the_stream_completes()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, CannedSseBody);
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "claude-3-5-sonnet-20241022", ["messages"] = new JsonArray() };
        var writer = new FakeStreamResponseWriter();

        var usage = await client.StreamChatCompletionAsync(request, "sk-ant-test", writer, CancellationToken.None);

        Assert.Equal(new StreamUsage(8, 3), usage);
    }

    [Fact]
    public async Task Sets_stream_true_on_the_translated_anthropic_request()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, CannedSseBody);
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "claude-3-5-sonnet-20241022", ["messages"] = new JsonArray() };
        var writer = new FakeStreamResponseWriter();

        await client.StreamChatCompletionAsync(request, "sk-ant-test", writer, CancellationToken.None);

        Assert.Contains("\"stream\":true", handler.LastRequestBody);
    }

    [Fact]
    public async Task Switches_to_a_translated_json_error_response_when_the_provider_rejects_the_request_before_streaming()
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "claude-3-5-sonnet-20241022", ["messages"] = new JsonArray() };
        var writer = new FakeStreamResponseWriter();

        var usage = await client.StreamChatCompletionAsync(request, "sk-ant-bad", writer, CancellationToken.None);

        Assert.Null(usage);
        Assert.Equal(401, writer.StatusCode);
        Assert.Equal("application/json", writer.ContentType);
        var body = JsonNode.Parse(writer.BodyAsString());
        Assert.Equal("invalid x-api-key", body?["error"]?["message"]?.GetValue<string>());
    }
}
