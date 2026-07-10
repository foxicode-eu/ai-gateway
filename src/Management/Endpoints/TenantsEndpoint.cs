using Core.Entities;
using Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Management.Endpoints;

public static class TenantsEndpoint
{
    public static IEndpointRouteBuilder MapTenants(this IEndpointRouteBuilder app)
    {
        app.MapPost("/tenants", CreateAsync);
        app.MapGet("/tenants/{tenantId:guid}", GetAsync);
        return app;
    }

    public sealed record CreateTenantRequest(string Name);

    private static async Task<IResult> CreateAsync(
        CreateTenantRequest request, GatewayDbContext dbContext, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = new { message = "Tenant name is required." } });
        }

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = request.Name, CreatedAtUtc = DateTimeOffset.UtcNow };
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/tenants/{tenant.Id}",
            new { id = tenant.Id, name = tenant.Name, createdAtUtc = tenant.CreatedAtUtc });
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId, GatewayDbContext dbContext, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        return tenant is null
            ? Results.NotFound()
            : Results.Ok(new { id = tenant.Id, name = tenant.Name, createdAtUtc = tenant.CreatedAtUtc });
    }
}
