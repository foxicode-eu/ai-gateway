using Core.Alerting;
using Core.Persistence;
using Core.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Api.Alerting;

/// <summary>
/// Checks, after usage has been recorded for a request, whether a tenant just crossed one of its configured
/// quota-alert thresholds and fires a webhook if so. Opt-in per tenant — both <c>AlertWebhookUrl</c> and
/// <c>AlertThresholdPercentages</c> must be set (see <c>Core.Entities.Tenant</c>) and a token quota must be
/// configured, since thresholds are percentages of it.
/// </summary>
public sealed class QuotaAlertGate(
    ITokenRateLimiter rateLimiter,
    IRateLimitStore rateLimitStore,
    GatewayDbContext dbContext,
    IOptions<RateLimitingOptions> options,
    IQuotaAlertSender alertSender,
    TimeProvider timeProvider,
    ILogger<QuotaAlertGate> logger)
{
    public async Task CheckAndAlertAsync(Guid tenantId, int? tenantQuota, CancellationToken cancellationToken)
    {
        if (tenantQuota is not int quota || quota <= 0)
        {
            return;
        }

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.AlertWebhookUrl, t.AlertThresholdPercentages })
            .FirstAsync(cancellationToken);

        if (tenant.AlertWebhookUrl is not { Length: > 0 } webhookUrl
            || tenant.AlertThresholdPercentages is not { Length: > 0 } thresholds)
        {
            return;
        }

        var window = TimeSpan.FromSeconds(options.Value.WindowSeconds);

        // Re-uses the same blended sliding-window estimate `RateLimitGate` enforces admission with — an
        // approximation, not an exact count, which is the right trade-off for "approaching quota" alerting
        // (not a hard block).
        var status = await rateLimiter.CheckAsync(RateLimitKeys.TenantKey(tenantId), quota, window, cancellationToken);
        var usagePercentage = status.EstimatedUsage / quota * 100.0;

        var crossedThreshold = thresholds.Where(t => usagePercentage >= t).DefaultIfEmpty(0).Max();
        if (crossedThreshold == 0)
        {
            return;
        }

        var windowIndex = timeProvider.GetUtcNow().ToUnixTimeSeconds() / (long)window.TotalSeconds;
        var stateKey = $"alert:tenant:{tenantId:N}:{windowIndex}";
        var alreadyAlerted = await rateLimitStore.GetAsync(stateKey, cancellationToken);

        if (alreadyAlerted >= crossedThreshold)
        {
            // Already fired for this threshold (or a higher one) within this window — don't re-alert on every
            // subsequent request.
            return;
        }

        // IRateLimitStore only exposes increment/get, no "set" — moving the stored value up to
        // `crossedThreshold` is expressed as "add the gap". Kept alive for two windows, same as rate-limit
        // counters, so it reads as "already alerted" for the rest of the window it was set in.
        await rateLimitStore.IncrementAsync(stateKey, crossedThreshold - alreadyAlerted, window * 2, cancellationToken);

        var payload = new QuotaAlertPayload(
            tenantId, crossedThreshold, usagePercentage, quota, (int)status.EstimatedUsage, timeProvider.GetUtcNow());

        try
        {
            await alertSender.SendAsync(webhookUrl, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            // A tenant's misconfigured/unreachable webhook must never break the chat-completion request that
            // triggered it.
            logger.LogWarning(ex, "Failed to deliver quota alert webhook for tenant {TenantId}", tenantId);
        }
    }
}
