using System.Net;
using System.Text.Json.Nodes;
using Core.Providers;
using Xunit;

namespace Core.Tests.Providers;

public class OpenAiProviderClientStreamingTests
{
    private const string CannedSseBody =
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}}\n\n" +
        "data: [DONE]\n\n";

    private static OpenAiProviderClient CreateClient(FakeHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        return new OpenAiProviderClient(httpClient);
    }

    [Fact]
    public async Task Forwards_the_upstream_sse_bytes_unchanged()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, CannedSseBody);
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "gpt-4o-mini", ["messages"] = new JsonArray(), ["stream"] = true };
        var writer = new FakeStreamResponseWriter();

        await client.StreamChatCompletionAsync(request, "sk-test", writer, CancellationToken.None);

        var written = writer.BodyAsString();
        Assert.Contains("\"content\":\"Hi\"", written);
        Assert.EndsWith("data: [DONE]\n\n", written);
        Assert.Equal(200, writer.StatusCode);
    }

    [Fact]
    public async Task Extracts_usage_from_the_final_chunk()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, CannedSseBody);
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "gpt-4o-mini", ["stream"] = true };
        var writer = new FakeStreamResponseWriter();

        var usage = await client.StreamChatCompletionAsync(request, "sk-test", writer, CancellationToken.None);

        Assert.Equal(new StreamUsage(10, 2), usage);
    }

    [Fact]
    public async Task Requests_usage_via_stream_options()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, CannedSseBody);
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "gpt-4o-mini", ["stream"] = true };
        var writer = new FakeStreamResponseWriter();

        await client.StreamChatCompletionAsync(request, "sk-test", writer, CancellationToken.None);

        Assert.Contains("\"stream_options\":{\"include_usage\":true}", handler.LastRequestBody);
    }

    [Fact]
    public async Task Returns_null_usage_when_the_provider_never_sends_a_usage_chunk()
    {
        const string bodyWithoutUsage =
            "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, bodyWithoutUsage);
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "gpt-4o-mini", ["stream"] = true };
        var writer = new FakeStreamResponseWriter();

        var usage = await client.StreamChatCompletionAsync(request, "sk-test", writer, CancellationToken.None);

        Assert.Null(usage);
    }

    [Fact]
    public async Task Switches_to_a_json_error_response_when_the_provider_rejects_the_request_before_streaming()
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided","type":"invalid_request_error"}}""");
        var client = CreateClient(handler);
        var request = new JsonObject { ["model"] = "gpt-4o-mini", ["stream"] = true };
        var writer = new FakeStreamResponseWriter();

        var usage = await client.StreamChatCompletionAsync(request, "sk-bad-key", writer, CancellationToken.None);

        Assert.Null(usage);
        Assert.Equal(401, writer.StatusCode);
        Assert.Equal("application/json", writer.ContentType);
        var body = JsonNode.Parse(writer.BodyAsString());
        Assert.Equal("Incorrect API key provided", body?["error"]?["message"]?.GetValue<string>());
    }
}
