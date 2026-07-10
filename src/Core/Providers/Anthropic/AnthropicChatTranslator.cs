using System.Text.Json.Nodes;

namespace Core.Providers.Anthropic;

/// <summary>
/// Translates between the gateway's public OpenAI-shaped chat completion contract and Anthropic's native
/// Messages API shape, so tenants can point an OpenAI-compatible client at a Claude model without knowing the
/// difference. Deliberately narrow: handles the common case (string message content, one text block back) and
/// is expected to grow as real usage surfaces gaps — see the "known limitations" note on each method.
/// </summary>
public static class AnthropicChatTranslator
{
    /// <summary>Anthropic requires max_tokens; OpenAI callers often omit it. This is the fallback when they do.</summary>
    public const int DefaultMaxTokens = 1024;

    /// <summary>
    /// Known limitation: only string message content is supported. Structured/multimodal content (OpenAI's
    /// array-of-parts form) is passed through as-is, which Anthropic will likely reject — revisit if/when
    /// multimodal support is needed.
    /// </summary>
    public static JsonObject ToAnthropicRequest(JsonObject openAiRequest)
    {
        var model = openAiRequest["model"]?.GetValue<string>()
            ?? throw new ArgumentException("Request must include a 'model' field.", nameof(openAiRequest));

        var anthropicMessages = new JsonArray();
        var systemParts = new List<string>();

        if (openAiRequest["messages"] is JsonArray messages)
        {
            foreach (var messageNode in messages)
            {
                if (messageNode is not JsonObject message)
                {
                    continue;
                }

                var role = message["role"]?.GetValue<string>();
                var content = message["content"]?.GetValue<string>() ?? "";

                if (role == "system")
                {
                    systemParts.Add(content);
                    continue;
                }

                // Anthropic only recognizes "user" and "assistant" roles in the messages array.
                anthropicMessages.Add(new JsonObject
                {
                    ["role"] = role == "assistant" ? "assistant" : "user",
                    ["content"] = content,
                });
            }
        }

        var anthropicRequest = new JsonObject
        {
            ["model"] = model,
            ["messages"] = anthropicMessages,
            ["max_tokens"] = openAiRequest["max_tokens"]?.GetValue<int>() ?? DefaultMaxTokens,
        };

        if (systemParts.Count > 0)
        {
            anthropicRequest["system"] = string.Join("\n\n", systemParts);
        }

        if (openAiRequest["temperature"] is JsonValue temperature)
        {
            anthropicRequest["temperature"] = temperature.DeepClone();
        }

        return anthropicRequest;
    }

    /// <summary>
    /// Known limitation: only the first text content block is used to build the OpenAI-shaped message; a
    /// response with multiple text blocks is concatenated, and non-text blocks (e.g. tool use) are ignored.
    /// </summary>
    public static JsonObject ToOpenAiResponse(JsonObject anthropicResponse)
    {
        if (anthropicResponse["type"]?.GetValue<string>() == "error")
        {
            var errorMessage = anthropicResponse["error"]?["message"]?.GetValue<string>() ?? "Unknown provider error.";
            var errorType = anthropicResponse["error"]?["type"]?.GetValue<string>() ?? "provider_error";
            return new JsonObject
            {
                ["error"] = new JsonObject { ["message"] = errorMessage, ["type"] = errorType },
            };
        }

        var textContent = string.Concat(
            (anthropicResponse["content"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Where(block => block["type"]?.GetValue<string>() == "text")
                .Select(block => block["text"]?.GetValue<string>() ?? ""));

        var inputTokens = anthropicResponse["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
        var outputTokens = anthropicResponse["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;

        return new JsonObject
        {
            ["id"] = anthropicResponse["id"]?.GetValue<string>(),
            ["object"] = "chat.completion",
            ["model"] = anthropicResponse["model"]?.GetValue<string>(),
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = textContent },
                    ["finish_reason"] = MapStopReason(anthropicResponse["stop_reason"]?.GetValue<string>()),
                },
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = inputTokens,
                ["completion_tokens"] = outputTokens,
                ["total_tokens"] = inputTokens + outputTokens,
            },
        };
    }

    /// <summary>Internal so <see cref="AnthropicStreamTranslator"/> can reuse the same mapping.</summary>
    internal static string MapStopReason(string? anthropicStopReason) => anthropicStopReason switch
    {
        "end_turn" or "stop_sequence" => "stop",
        "max_tokens" => "length",
        null => "stop",
        _ => anthropicStopReason,
    };
}
