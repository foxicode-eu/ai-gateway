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

    /// <summary>
    /// Webhook URL to POST quota-threshold alerts to (see <c>Core.Alerting</c>). Null disables alerting for this
    /// tenant, regardless of <see cref="AlertThresholdPercentages"/> — both must be set for alerts to fire.
    /// </summary>
    public string? AlertWebhookUrl { get; set; }

    /// <summary>
    /// Percentages of <see cref="TokenQuotaPerWindow"/> (e.g. [80, 100]) at which a quota alert should fire.
    /// Null/empty means no thresholds configured. Meaningless without <see cref="TokenQuotaPerWindow"/> also set
    /// — there's no quota to be a percentage of otherwise.
    /// </summary>
    public int[]? AlertThresholdPercentages { get; set; }

    public List<ApiKey> ApiKeys { get; set; } = [];
}
