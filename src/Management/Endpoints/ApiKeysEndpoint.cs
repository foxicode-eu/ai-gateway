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
        tenantsGroup.MapGet("/{tenantId:guid}/api-keys", ListAsync);
        tenantsGroup.MapDelete("/{tenantId:guid}/api-keys/{apiKeyId:guid}", RevokeAsync);
        tenantsGroup.MapPatch("/{tenantId:guid}/api-keys/{apiKeyId:guid}", UpdateAsync);
        return tenantsGroup;
    }

    public sealed record CreateApiKeyRequest(string Name, int? TokenQuotaPerWindow = null);

    public sealed record UpdateApiKeyRequest(int? TokenQuotaPerWindow);

    private static async Task<IResult> CreateAsync(
        Guid tenantId, CreateApiKeyRequest request, GatewayDbContext dbContext, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = new { message = "API key name is required." } });
        }

        if (request.TokenQuotaPerWindow is < 0)
        {
            return Results.BadRequest(new { error = new { message = "tokenQuotaPerWindow must be non-negative." } });
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
            TokenQuotaPerWindow = request.TokenQuotaPerWindow,
        };
        dbContext.ApiKeys.Add(apiKey);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/tenants/{tenantId}/api-keys/{apiKey.Id}",
            new
            {
                id = apiKey.Id,
                name = apiKey.Name,
                key = plaintextKey,
                createdAtUtc = apiKey.CreatedAtUtc,
                tokenQuotaPerWindow = apiKey.TokenQuotaPerWindow,
            });
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId, GatewayDbContext dbContext, CancellationToken cancellationToken)
    {
        var tenantExists = await dbContext.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken);
        if (!tenantExists)
        {
            return Results.NotFound(new { error = new { message = "Tenant not found." } });
        }

        // Never the hash or plaintext — this is a management listing, not a credential export.
        var apiKeys = await dbContext.ApiKeys
            .Where(k => k.TenantId == tenantId)
            .OrderBy(k => k.Name)
            .Select(k => new
            {
                id = k.Id,
                name = k.Name,
                createdAtUtc = k.CreatedAtUtc,
                revokedAtUtc = k.RevokedAtUtc,
                tokenQuotaPerWindow = k.TokenQuotaPerWindow,
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(apiKeys);
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

    private static async Task<IResult> UpdateAsync(
        Guid tenantId, Guid apiKeyId, UpdateApiKeyRequest request, GatewayDbContext dbContext, CancellationToken cancellationToken)
    {
        if (request.TokenQuotaPerWindow is < 0)
        {
            return Results.BadRequest(new { error = new { message = "tokenQuotaPerWindow must be non-negative." } });
        }

        var apiKey = await dbContext.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == apiKeyId && k.TenantId == tenantId, cancellationToken);

        if (apiKey is null)
        {
            return Results.NotFound();
        }

        apiKey.TokenQuotaPerWindow = request.TokenQuotaPerWindow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new
        {
            id = apiKey.Id,
            name = apiKey.Name,
            tokenQuotaPerWindow = apiKey.TokenQuotaPerWindow,
        });
    }
}
