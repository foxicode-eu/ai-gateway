using Core.Entities;
using Core.Persistence;

namespace Api.Observability;

/// <summary>
/// Persists a metadata-only <see cref="UsageEvent"/> per chat completion request. Never given prompt/completion
/// content — see ARCHITECTURE.md's "Payload privacy" rule and <see cref="UsageEvent"/>'s doc comment.
/// </summary>
public sealed class UsageEventRecorder(GatewayDbContext dbContext)
{
    public async Task RecordAsync(
        Guid tenantId,
        Guid? apiKeyId,
        string provider,
        string model,
        bool streamed,
        int statusCode,
        int promptTokens,
        int completionTokens,
        long latencyMs,
        CancellationToken cancellationToken)
    {
        dbContext.UsageEvents.Add(new UsageEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ApiKeyId = apiKeyId,
            Provider = provider,
            Model = model,
            Streamed = streamed,
            StatusCode = statusCode,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            LatencyMs = latencyMs,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
