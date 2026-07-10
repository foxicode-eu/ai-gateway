namespace Core.Entities;

/// <summary>
/// A metadata-only record of one chat completion request. Never stores prompt/completion content — see
/// ARCHITECTURE.md's "Payload privacy" rule. Written once a request has resolved to a tenant + provider (i.e.
/// not for requests that fail basic body validation before that point — see ChatCompletionsEndpoint).
/// </summary>
public class UsageEvent
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Null for JWT-authenticated requests — that credential model has no notion of "which API key".</summary>
    public Guid? ApiKeyId { get; set; }

    public required string Provider { get; set; }

    public required string Model { get; set; }

    public bool Streamed { get; set; }

    public int StatusCode { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public long LatencyMs { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
