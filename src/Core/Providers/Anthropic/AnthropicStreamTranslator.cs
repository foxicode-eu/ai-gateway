using System.Text.Json.Nodes;

namespace Core.Providers.Anthropic;

/// <summary>
/// Translates Anthropic's Messages API SSE events into OpenAI-shaped <c>chat.completion.chunk</c> objects,
/// one Anthropic SSE frame (<c>event: ...</c> + its <c>data: ...</c>) at a time. Pure/stateful, no I/O — the
/// caller owns reading the upstream SSE stream and writing translated chunks to the client.
///
/// Known limitations (matching <see cref="AnthropicChatTranslator"/>'s non-streaming translation): only text
/// content deltas are translated; tool-use and other content block types are ignored. A mid-stream
/// <c>event: error</c> has no standard OpenAI streaming equivalent (OpenAI errors are a non-200 response before
/// any streaming starts, not a mid-stream event) — callers should treat it as fatal and stop.
/// </summary>
public sealed class AnthropicStreamTranslator
{
    private string? _messageId;
    private string? _model;
    private int _inputTokens;
    private int _outputTokens;

    /// <summary>True once <c>message_stop</c> has been processed — the caller should send <c>[DONE]</c> and stop reading.</summary>
    public bool IsDone { get; private set; }

    /// <summary>Available once at least one event carrying usage has been processed.</summary>
    public StreamUsage? FinalUsage => _messageId is null ? null : new StreamUsage(_inputTokens, _outputTokens);

    /// <returns>An OpenAI-shaped chunk to forward, or null if this event doesn't translate to one.</returns>
    public JsonObject? ProcessEvent(string eventType, JsonObject data)
    {
        switch (eventType)
        {
            case "message_start":
                var message = data["message"] as JsonObject;
                _messageId = message?["id"]?.GetValue<string>();
                _model = message?["model"]?.GetValue<string>();
                _inputTokens = message?["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
                return BuildChunk(new JsonObject { ["role"] = "assistant" }, finishReason: null);

            case "content_block_delta":
                var delta = data["delta"] as JsonObject;
                if (delta?["type"]?.GetValue<string>() != "text_delta")
                {
                    return null;
                }

                var text = delta["text"]?.GetValue<string>() ?? "";
                return BuildChunk(new JsonObject { ["content"] = text }, finishReason: null);

            case "message_delta":
                if (data["usage"]?["output_tokens"] is JsonValue outputTokens)
                {
                    _outputTokens = outputTokens.GetValue<int>();
                }

                var stopReason = data["delta"]?["stop_reason"]?.GetValue<string>();
                return stopReason is null ? null : BuildChunk(new JsonObject(), AnthropicChatTranslator.MapStopReason(stopReason));

            case "message_stop":
                IsDone = true;
                return null;

            default:
                // content_block_start, content_block_stop, ping, error, and anything else Anthropic adds later.
                return null;
        }
    }

    private JsonObject BuildChunk(JsonObject delta, string? finishReason) => new()
    {
        ["id"] = _messageId,
        ["object"] = "chat.completion.chunk",
        ["model"] = _model,
        ["choices"] = new JsonArray
        {
            new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finishReason },
        },
    };
}
