using System.Text.Json.Nodes;
using Core.Providers.Anthropic;
using Xunit;

namespace Core.Tests.Providers.Anthropic;

public class AnthropicChatTranslatorTests
{
    [Fact]
    public void ToAnthropicRequest_moves_system_message_out_of_the_messages_array()
    {
        var openAiRequest = new JsonObject
        {
            ["model"] = "claude-3-5-sonnet-20241022",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "Be concise." },
                new JsonObject { ["role"] = "user", ["content"] = "Hello" },
            },
        };

        var anthropicRequest = AnthropicChatTranslator.ToAnthropicRequest(openAiRequest);

        Assert.Equal("Be concise.", anthropicRequest["system"]?.GetValue<string>());
        var messages = Assert.IsType<JsonArray>(anthropicRequest["messages"]);
        var message = Assert.Single(messages);
        Assert.Equal("user", message!["role"]?.GetValue<string>());
        Assert.Equal("Hello", message["content"]?.GetValue<string>());
    }

    [Fact]
    public void ToAnthropicRequest_combines_multiple_system_messages()
    {
        var openAiRequest = new JsonObject
        {
            ["model"] = "claude-3-5-sonnet-20241022",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "First." },
                new JsonObject { ["role"] = "system", ["content"] = "Second." },
            },
        };

        var anthropicRequest = AnthropicChatTranslator.ToAnthropicRequest(openAiRequest);

        Assert.Equal("First.\n\nSecond.", anthropicRequest["system"]?.GetValue<string>());
    }

    [Fact]
    public void ToAnthropicRequest_defaults_max_tokens_when_not_provided()
    {
        var openAiRequest = new JsonObject { ["model"] = "claude-3-5-sonnet-20241022", ["messages"] = new JsonArray() };

        var anthropicRequest = AnthropicChatTranslator.ToAnthropicRequest(openAiRequest);

        Assert.Equal(AnthropicChatTranslator.DefaultMaxTokens, anthropicRequest["max_tokens"]?.GetValue<int>());
    }

    [Fact]
    public void ToAnthropicRequest_passes_through_explicit_max_tokens_and_temperature()
    {
        var openAiRequest = new JsonObject
        {
            ["model"] = "claude-3-5-sonnet-20241022",
            ["messages"] = new JsonArray(),
            ["max_tokens"] = 512,
            ["temperature"] = 0.2,
        };

        var anthropicRequest = AnthropicChatTranslator.ToAnthropicRequest(openAiRequest);

        Assert.Equal(512, anthropicRequest["max_tokens"]?.GetValue<int>());
        Assert.Equal(0.2, anthropicRequest["temperature"]?.GetValue<double>());
    }

    [Fact]
    public void ToAnthropicRequest_throws_when_model_is_missing()
    {
        var openAiRequest = new JsonObject { ["messages"] = new JsonArray() };

        Assert.Throws<ArgumentException>(() => AnthropicChatTranslator.ToAnthropicRequest(openAiRequest));
    }

    [Fact]
    public void ToOpenAiResponse_maps_text_content_and_usage_into_openai_chat_completion_shape()
    {
        var anthropicResponse = new JsonObject
        {
            ["id"] = "msg_123",
            ["model"] = "claude-3-5-sonnet-20241022",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Hi there!" } },
            ["stop_reason"] = "end_turn",
            ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 },
        };

        var openAiResponse = AnthropicChatTranslator.ToOpenAiResponse(anthropicResponse);

        Assert.Equal("msg_123", openAiResponse["id"]?.GetValue<string>());
        Assert.Equal("chat.completion", openAiResponse["object"]?.GetValue<string>());
        var choice = Assert.Single(Assert.IsType<JsonArray>(openAiResponse["choices"]))!;
        Assert.Equal("assistant", choice["message"]?["role"]?.GetValue<string>());
        Assert.Equal("Hi there!", choice["message"]?["content"]?.GetValue<string>());
        Assert.Equal("stop", choice["finish_reason"]?.GetValue<string>());
        Assert.Equal(10, openAiResponse["usage"]?["prompt_tokens"]?.GetValue<int>());
        Assert.Equal(5, openAiResponse["usage"]?["completion_tokens"]?.GetValue<int>());
        Assert.Equal(15, openAiResponse["usage"]?["total_tokens"]?.GetValue<int>());
    }

    [Theory]
    [InlineData("max_tokens", "length")]
    [InlineData("end_turn", "stop")]
    [InlineData("stop_sequence", "stop")]
    [InlineData("tool_use", "tool_use")]
    public void ToOpenAiResponse_maps_stop_reason_to_finish_reason(string anthropicStopReason, string expectedFinishReason)
    {
        var anthropicResponse = new JsonObject
        {
            ["id"] = "msg_123",
            ["content"] = new JsonArray(),
            ["stop_reason"] = anthropicStopReason,
        };

        var openAiResponse = AnthropicChatTranslator.ToOpenAiResponse(anthropicResponse);

        var choice = Assert.Single(Assert.IsType<JsonArray>(openAiResponse["choices"]))!;
        Assert.Equal(expectedFinishReason, choice["finish_reason"]?.GetValue<string>());
    }

    [Fact]
    public void ToOpenAiResponse_translates_anthropic_error_shape_into_openai_error_shape()
    {
        var anthropicErrorResponse = new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = "invalid_request_error", ["message"] = "model: field required" },
        };

        var openAiResponse = AnthropicChatTranslator.ToOpenAiResponse(anthropicErrorResponse);

        Assert.Equal("model: field required", openAiResponse["error"]?["message"]?.GetValue<string>());
        Assert.Equal("invalid_request_error", openAiResponse["error"]?["type"]?.GetValue<string>());
    }

    [Fact]
    public void ToOpenAiResponse_concatenates_multiple_text_blocks()
    {
        var anthropicResponse = new JsonObject
        {
            ["id"] = "msg_123",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "Part one. " },
                new JsonObject { ["type"] = "text", ["text"] = "Part two." },
            },
            ["stop_reason"] = "end_turn",
        };

        var openAiResponse = AnthropicChatTranslator.ToOpenAiResponse(anthropicResponse);

        var choice = Assert.Single(Assert.IsType<JsonArray>(openAiResponse["choices"]))!;
        Assert.Equal("Part one. Part two.", choice["message"]?["content"]?.GetValue<string>());
    }
}
