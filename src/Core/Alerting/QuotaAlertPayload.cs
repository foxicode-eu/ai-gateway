namespace Core.Alerting;

/// <summary>The JSON body POSTed to a tenant's configured alert webhook.</summary>
public sealed record QuotaAlertPayload(
    Guid TenantId,
    int ThresholdPercentage,
    double UsagePercentage,
    int QuotaLimit,
    int CurrentUsage,
    DateTimeOffset TimestampUtc);
