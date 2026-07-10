namespace Core.Entities;

public class Tenant
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>
    /// Max tokens (prompt + completion) this tenant may use per rate-limit window (see
    /// <c>RateLimiting:WindowSeconds</c> config — this value is a count, the window duration is global, not
    /// per-tenant). Null means unlimited.
    /// </summary>
    public int? TokenQuotaPerWindow { get; set; }

    public List<ApiKey> ApiKeys { get; set; } = [];
}
