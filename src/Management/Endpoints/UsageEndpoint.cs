using Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Management.Endpoints;

public static class UsageEndpoint
{
    public static IEndpointRouteBuilder MapUsage(this IEndpointRouteBuilder tenantsGroup)
    {
        tenantsGroup.MapGet("/{tenantId:guid}/usage", GetAsync);
        return tenantsGroup;
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId,
        double? sinceHours,
        GatewayDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var effectiveSinceHours = sinceHours is > 0 ? sinceHours.Value : 24;

        var tenantExists = await dbContext.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken);
        if (!tenantExists)
        {
            return Results.NotFound();
        }

        var untilUtc = DateTimeOffset.UtcNow;
        var sinceUtc = untilUtc.AddHours(-effectiveSinceHours);

        var events = dbContext.UsageEvents
            .Where(e => e.TenantId == tenantId && e.CreatedAtUtc >= sinceUtc && e.CreatedAtUtc <= untilUtc);

        var byProvider = await events
            .GroupBy(e => e.Provider)
            .Select(g => new
            {
                provider = g.Key,
                requests = g.Count(),
                promptTokens = g.Sum(e => e.PromptTokens),
                completionTokens = g.Sum(e => e.CompletionTokens),
            })
            .ToListAsync(cancellationToken);

        var totalRequests = byProvider.Sum(p => p.requests);
        var totalPromptTokens = byProvider.Sum(p => p.promptTokens);
        var totalCompletionTokens = byProvider.Sum(p => p.completionTokens);
        var errorCount = await events.CountAsync(e => e.StatusCode >= 400, cancellationToken);

        return Results.Ok(new
        {
            tenantId,
            sinceUtc,
            untilUtc,
            totalRequests,
            totalPromptTokens,
            totalCompletionTokens,
            totalTokens = totalPromptTokens + totalCompletionTokens,
            errorCount,
            byProvider,
        });
    }
}
