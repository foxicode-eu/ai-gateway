using Core.Entities;
using Core.Persistence;
using Core.Security;
using Microsoft.EntityFrameworkCore;

namespace Management.Endpoints;

public static class ApiKeysEndpoint
{
    public static IEndpointRouteBuilder MapApiKeys(this IEndpointRouteBuilder tenantsGroup)
    {
        tenantsGroup.MapPost("/{tenantId:guid}/api-keys", CreateAsync);
        tenantsGroup.MapDelete("/{tenantId:guid}/api-keys/{apiKeyId:guid}", RevokeAsync);
        return tenantsGroup;
    }

    public sealed record CreateApiKeyRequest(string Name);

    private static async Task<IResult> CreateAsync(
        Guid tenantId, CreateApiKeyRequest request, GatewayDbContext dbContext, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = new { message = "API key name is required." } });
        }

        var tenantExists = await dbContext.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken);
        if (!tenantExists)
        {
            return Results.NotFound(new { error = new { message = "Tenant not found." } });
        }

        // Shown only in this response — the gateway stores just the hash and cannot recover the plaintext later.
        var plaintextKey = ApiKeyGenerator.GenerateSecret();
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = request.Name,
            KeyHash = ApiKeyGenerator.Hash(plaintextKey),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.ApiKeys.Add(apiKey);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/tenants/{tenantId}/api-keys/{apiKey.Id}",
            new { id = apiKey.Id, name = apiKey.Name, key = plaintextKey, createdAtUtc = apiKey.CreatedAtUtc });
    }

    private static async Task<IResult> RevokeAsync(
        Guid tenantId, Guid apiKeyId, GatewayDbContext dbContext, CancellationToken cancellationToken)
    {
        var apiKey = await dbContext.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == apiKeyId && k.TenantId == tenantId, cancellationToken);

        if (apiKey is null)
        {
            return Results.NotFound();
        }

        apiKey.RevokedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }
}
