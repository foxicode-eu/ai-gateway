namespace Core.RateLimiting;

/// <summary>
/// The store-key format for tenant/API-key rate-limit counters. Extracted so both <c>Api.RateLimiting.RateLimitGate</c>
/// (which enforces/records against these keys) and <c>Api.Alerting.QuotaAlertGate</c> (which re-checks the same
/// counters to decide whether a quota-threshold alert should fire) can't drift apart on the exact string format.
/// </summary>
public static class RateLimitKeys
{
    public static string TenantKey(Guid tenantId) => $"ratelimit:tenant:{tenantId:N}";

    public static string ApiKeyKey(Guid apiKeyId) => $"ratelimit:apikey:{apiKeyId:N}";
}
