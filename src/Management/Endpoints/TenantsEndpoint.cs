using Core.Entities;
using Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Management.Endpoints;

public static class TenantsEndpoint
{
    public static IEndpointRouteBuilder MapTenants(this IEndpointRouteBuilder tenantsGroup)
    {
        tenantsGroup.MapPost("", CreateAsync);
        tenantsGroup.MapGet("", ListAsync);
        tenantsGroup.MapGet("/{tenantId:guid}", GetAsync);
        tenantsGroup.MapPatch("/{tenantId:guid}", UpdateAsync);
        return tenantsGroup;
    }

    public sealed record CreateTenantRequest(
        string Name, int? TokenQuotaPerWindow = null, string? AlertWebhookUrl = null, int[]? AlertThresholdPercentages = null);

    public sealed record UpdateTenantRequest(
        int? TokenQuotaPerWindow, string? AlertWebhookUrl = null, int[]? AlertThresholdPercentages = null);

    private static async Task<IResult> CreateAsync(
        CreateTenantRequest request, GatewayDbContext dbContext, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = new { message = "Tenant name is required." } });
        }

        if (request.TokenQuotaPerWindow is < 0)
        {
            return Results.BadRequest(new { error = new { message = "tokenQuotaPerWindow must be non-negative." } });
        }

        if (ValidateAlertFields(request.AlertWebhookUrl, request.AlertThresholdPercentages) is { } error)
        {
            return error;
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            TokenQuotaPerWindow = request.TokenQuotaPerWindow,
            AlertWebhookUrl = request.AlertWebhookUrl,
            AlertThresholdPercentages = request.AlertThresholdPercentages,
        };
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created($"/tenants/{tenant.Id}", ToResponse(tenant));
    }

    private static async Task<IResult> ListAsync(GatewayDbContext dbContext, CancellationToken cancellationToken)
    {
        var tenants = await dbContext.Tenants
            .OrderBy(t => t.Name)
            .Select(t => new { id = t.Id, name = t.Name, createdAtUtc = t.CreatedAtUtc, tokenQuotaPerWindow = t.TokenQuotaPerWindow })
            .ToListAsync(cancellationToken);

        return Results.Ok(tenants);
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId, GatewayDbContext dbContext, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        return tenant is null ? Results.NotFound() : Results.Ok(ToResponse(tenant));
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId, UpdateTenantRequest request, GatewayDbContext dbContext, CancellationToken cancellationToken)
    {
        if (request.TokenQuotaPerWindow is < 0)
        {
            return Results.BadRequest(new { error = new { message = "tokenQuotaPerWindow must be non-negative." } });
        }

        if (ValidateAlertFields(request.AlertWebhookUrl, request.AlertThresholdPercentages) is { } error)
        {
            return error;
        }

        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return Results.NotFound();
        }

        tenant.TokenQuotaPerWindow = request.TokenQuotaPerWindow;
        tenant.AlertWebhookUrl = request.AlertWebhookUrl;
        tenant.AlertThresholdPercentages = request.AlertThresholdPercentages;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(ToResponse(tenant));
    }

    /// <summary>Applied as given, same "not unset-means-don't-change" rule as <c>TokenQuotaPerWindow</c> —
    /// a <c>PATCH</c> with both fields null clears alerting back to disabled.</summary>
    private static IResult? ValidateAlertFields(string? webhookUrl, int[]? thresholdPercentages)
    {
        if (webhookUrl is { Length: > 0 } url
            && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")))
        {
            return Results.BadRequest(new { error = new { message = "alertWebhookUrl must be an absolute http(s) URL." } });
        }

        if (thresholdPercentages is { Length: > 0 } thresholds && thresholds.Any(t => t is < 1 or > 100))
        {
            return Results.BadRequest(new { error = new { message = "alertThresholdPercentages must each be between 1 and 100." } });
        }

        return null;
    }

    private static object ToResponse(Tenant tenant) => new
    {
        id = tenant.Id,
        name = tenant.Name,
        createdAtUtc = tenant.CreatedAtUtc,
        tokenQuotaPerWindow = tenant.TokenQuotaPerWindow,
        alertWebhookUrl = tenant.AlertWebhookUrl,
        alertThresholdPercentages = tenant.AlertThresholdPercentages,
    };
}
