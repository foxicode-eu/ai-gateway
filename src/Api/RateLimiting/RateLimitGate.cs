using Core.Persistence;
using Core.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.RateLimiting;

/// <summary>
/// Resolves a tenant's (and optionally an API key's) configured quotas from the DB and checks/records usage
/// against them via <see cref="ITokenRateLimiter"/>. Quotas are opt-in per tenant/key (null = unlimited) — see
/// <c>Core.Entities.Tenant.TokenQuotaPerWindow</c> / <c>Core.Entities.ApiKey.TokenQuotaPerWindow</c>.
/// </summary>
public sealed class RateLimitGate(ITokenRateLimiter rateLimiter, GatewayDbContext dbContext, IOptions<RateLimitingOptions> options)
{
    public async Task<RateLimitCheckResult> CheckAsync(Guid tenantId, Guid? apiKeyId, CancellationToken cancellationToken)
    {
        var window = Window;

        var tenantQuota = await dbContext.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.TokenQuotaPerWindow)
            .FirstAsync(cancellationToken);

        if (tenantQuota is int tenantLimit)
        {
            var status = await rateLimiter.CheckAsync(RateLimitKeys.TenantKey(tenantId), tenantLimit, window, cancellationToken);
            if (!status.IsAllowed)
            {
                return new RateLimitCheckResult(false, "tenant", tenantQuota, null);
            }
        }

        int? apiKeyQuota = null;
        if (apiKeyId is { } keyId)
        {
            apiKeyQuota = await dbContext.ApiKeys
                .AsNoTracking()
                .Where(k => k.Id == keyId)
                .Select(k => k.TokenQuotaPerWindow)
                .FirstOrDefaultAsync(cancellationToken);

            if (apiKeyQuota is int keyLimit)
            {
                var status = await rateLimiter.CheckAsync(RateLimitKeys.ApiKeyKey(keyId), keyLimit, window, cancellationToken);
                if (!status.IsAllowed)
                {
                    return new RateLimitCheckResult(false, "api_key", tenantQuota, apiKeyQuota);
                }
            }
        }

        return new RateLimitCheckResult(true, null, tenantQuota, apiKeyQuota);
    }

    public async Task RecordUsageAsync(Guid tenantId, Guid? apiKeyId, RateLimitCheckResult checkResult, int tokens, CancellationToken cancellationToken)
    {
        if (tokens <= 0)
        {
            return;
        }

        var window = Window;

        if (checkResult.TenantQuota.HasValue)
        {
            await rateLimiter.RecordUsageAsync(RateLimitKeys.TenantKey(tenantId), tokens, window, cancellationToken);
        }

        if (apiKeyId is { } keyId && checkResult.ApiKeyQuota.HasValue)
        {
            await rateLimiter.RecordUsageAsync(RateLimitKeys.ApiKeyKey(keyId), tokens, window, cancellationToken);
        }
    }

    private TimeSpan Window => TimeSpan.FromSeconds(options.Value.WindowSeconds);
}

/// <param name="TenantQuota">The tenant's configured quota, if any — null means unlimited. Passed to
/// <see cref="RateLimitGate.RecordUsageAsync"/> so it doesn't need a second DB round trip.</param>
/// <param name="ApiKeyQuota">Same, for the API key, if the request was authenticated with one.</param>
public sealed record RateLimitCheckResult(bool IsAllowed, string? BlockedScope, int? TenantQuota, int? ApiKeyQuota);
