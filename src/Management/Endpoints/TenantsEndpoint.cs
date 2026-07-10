using Core.Entities;
using Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Management.Endpoints;

public static class TenantsEndpoint
{
    public static IEndpointRouteBuilder MapTenants(this IEndpointRouteBuilder tenantsGroup)
    {
        tenantsGroup.MapPost("", CreateAsync);
        tenantsGroup.MapGet("/{tenantId:guid}", GetAsync);
        tenantsGroup.MapPatch("/{tenantId:guid}", UpdateAsync);
        return tenantsGroup;
    }

    public sealed record CreateTenantRequest(string Name, int? TokenQuotaPerWindow = null);

    public sealed record UpdateTenantRequest(int? TokenQuotaPerWindow);

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

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            TokenQuotaPerWindow = request.TokenQuotaPerWindow,
        };
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created($"/tenants/{tenant.Id}", ToResponse(tenant));
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

        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return Results.NotFound();
        }

        tenant.TokenQuotaPerWindow = request.TokenQuotaPerWindow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(ToResponse(tenant));
    }

    private static object ToResponse(Tenant tenant) => new
    {
        id = tenant.Id,
        name = tenant.Name,
        createdAtUtc = tenant.CreatedAtUtc,
        tokenQuotaPerWindow = tenant.TokenQuotaPerWindow,
    };
}
