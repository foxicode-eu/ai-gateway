using System.Text.Json.Nodes;
using Core.Providers.Anthropic;
using Xunit;

namespace Core.Tests.Providers.Anthropic;

public class AnthropicStreamTranslatorTests
{
    [Fact]
    public void Message_start_emits_a_role_only_chunk_and_captures_id_and_model()
    {
        var translator = new AnthropicStreamTranslator();
        var data = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["id"] = "msg_123",
                ["model"] = "claude-3-5-sonnet-20241022",
                ["usage"] = new JsonObject { ["input_tokens"] = 12 },
            },
        };

        var chunk = translator.ProcessEvent("message_start", data);

        Assert.NotNull(chunk);
        Assert.Equal("msg_123", chunk!["id"]?.GetValue<string>());
        Assert.Equal("chat.completion.chunk", chunk["object"]?.GetValue<string>());
        Assert.Equal("claude-3-5-sonnet-20241022", chunk["model"]?.GetValue<string>());
        var choice = Assert.Single(Assert.IsType<JsonArray>(chunk["choices"]))!;
        Assert.Equal("assistant", choice["delta"]?["role"]?.GetValue<string>());
        Assert.Null(choice["finish_reason"]);
    }

    [Fact]
    public void Content_block_delta_with_text_delta_emits_a_content_chunk()
    {
        var translator = new AnthropicStreamTranslator();
        translator.ProcessEvent("message_start", new JsonObject { ["message"] = new JsonObject { ["id"] = "msg_1" } });

        var chunk = translator.ProcessEvent("content_block_delta", new JsonObject
        {
            ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = "Hello" },
        });

        Assert.NotNull(chunk);
        var choice = Assert.Single(Assert.IsType<JsonArray>(chunk!["choices"]))!;
        Assert.Equal("Hello", choice["delta"]?["content"]?.GetValue<string>());
    }

    [Fact]
    public void Content_block_delta_with_a_non_text_delta_type_emits_nothing()
    {
        var translator = new AnthropicStreamTranslator();

        var chunk = translator.ProcessEvent("content_block_delta", new JsonObject
        {
            ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{}" },
        });

        Assert.Null(chunk);
    }

    [Fact]
    public void Message_delta_with_stop_reason_emits_a_finish_chunk_and_maps_the_reason()
    {
        var translator = new AnthropicStreamTranslator();

        var chunk = translator.ProcessEvent("message_delta", new JsonObject
        {
            ["delta"] = new JsonObject { ["stop_reason"] = "max_tokens" },
            ["usage"] = new JsonObject { ["output_tokens"] = 42 },
        });

        Assert.NotNull(chunk);
        var choice = Assert.Single(Assert.IsType<JsonArray>(chunk!["choices"]))!;
        Assert.Equal("length", choice["finish_reason"]?.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonObject>(choice["delta"]));
    }

    [Fact]
    public void Message_delta_without_a_stop_reason_emits_nothing_but_still_updates_usage()
    {
        var translator = new AnthropicStreamTranslator();
        translator.ProcessEvent("message_start", new JsonObject { ["message"] = new JsonObject { ["id"] = "msg_1" } });

        var chunk = translator.ProcessEvent("message_delta", new JsonObject
        {
            ["delta"] = new JsonObject(),
            ["usage"] = new JsonObject { ["output_tokens"] = 7 },
        });

        Assert.Null(chunk);
        Assert.Equal(7, translator.FinalUsage?.CompletionTokens);
    }

    [Fact]
    public void Message_stop_sets_is_done_and_emits_nothing()
    {
        var translator = new AnthropicStreamTranslator();

        var chunk = translator.ProcessEvent("message_stop", new JsonObject());

        Assert.Null(chunk);
        Assert.True(translator.IsDone);
    }

    [Fact]
    public void Unrecognized_events_are_ignored()
    {
        var translator = new AnthropicStreamTranslator();

        var chunk = translator.ProcessEvent("ping", new JsonObject());

        Assert.Null(chunk);
        Assert.False(translator.IsDone);
    }

    [Fact]
    public void Final_usage_combines_input_and_output_tokens_from_separate_events()
    {
        var translator = new AnthropicStreamTranslator();
        translator.ProcessEvent("message_start", new JsonObject
        {
            ["message"] = new JsonObject { ["id"] = "msg_1", ["usage"] = new JsonObject { ["input_tokens"] = 10 } },
        });
        translator.ProcessEvent("message_delta", new JsonObject
        {
            ["delta"] = new JsonObject { ["stop_reason"] = "end_turn" },
            ["usage"] = new JsonObject { ["output_tokens"] = 5 },
        });

        Assert.Equal(new Core.Providers.StreamUsage(10, 5), translator.FinalUsage);
    }

    [Fact]
    public void Final_usage_is_null_before_any_message_start()
    {
        var translator = new AnthropicStreamTranslator();

        Assert.Null(translator.FinalUsage);
    }

    [Fact]
    public void A_full_sequence_produces_the_expected_chunk_order_and_content()
    {
        var translator = new AnthropicStreamTranslator();
        var chunks = new List<JsonObject>();

        void Process(string eventType, JsonObject data)
        {
            var chunk = translator.ProcessEvent(eventType, data);
            if (chunk is not null)
            {
                chunks.Add(chunk);
            }
        }

        Process("message_start", new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["id"] = "msg_1", ["model"] = "claude-3-5-sonnet-20241022",
                ["usage"] = new JsonObject { ["input_tokens"] = 8 },
            },
        });
        Process("content_block_start", new JsonObject());
        Process("content_block_delta", new JsonObject { ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = "Hi" } });
        Process("content_block_delta", new JsonObject { ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = "!" } });
        Process("content_block_stop", new JsonObject());
        Process("message_delta", new JsonObject
        {
            ["delta"] = new JsonObject { ["stop_reason"] = "end_turn" },
            ["usage"] = new JsonObject { ["output_tokens"] = 3 },
        });
        Process("message_stop", new JsonObject());

        Assert.Equal(4, chunks.Count);
        Assert.Equal("assistant", chunks[0]["choices"]![0]!["delta"]!["role"]?.GetValue<string>());
        Assert.Equal("Hi", chunks[1]["choices"]![0]!["delta"]!["content"]?.GetValue<string>());
        Assert.Equal("!", chunks[2]["choices"]![0]!["delta"]!["content"]?.GetValue<string>());
        Assert.Equal("stop", chunks[3]["choices"]![0]!["finish_reason"]?.GetValue<string>());
        Assert.True(translator.IsDone);
        Assert.Equal(new Core.Providers.StreamUsage(8, 3), translator.FinalUsage);
    }
}
