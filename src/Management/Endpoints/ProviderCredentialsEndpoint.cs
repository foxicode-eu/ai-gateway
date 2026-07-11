using Core.Persistence;
using Core.Providers;
using Core.Secrets;
using Microsoft.EntityFrameworkCore;

namespace Management.Endpoints;

public static class ProviderCredentialsEndpoint
{
    public static IEndpointRouteBuilder MapProviderCredentials(this IEndpointRouteBuilder tenantsGroup)
    {
        tenantsGroup.MapPut("/{tenantId:guid}/providers/{providerName}", SetAsync);
        tenantsGroup.MapGet("/{tenantId:guid}/providers", ListAsync);
        return tenantsGroup;
    }

    public sealed record SetProviderCredentialRequest(string ApiKey);

    private static async Task<IResult> SetAsync(
        Guid tenantId,
        string providerName,
        SetProviderCredentialRequest request,
        GatewayDbContext dbContext,
        ISecretStore secretStore,
        IEnumerable<IProviderClient> providerClients,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return Results.BadRequest(new { error = new { message = "apiKey is required." } });
        }

        var knownProviderNames = providerClients.Select(c => c.ProviderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!knownProviderNames.Contains(providerName))
        {
            return Results.BadRequest(new
            {
                error = new { message = $"Unknown provider '{providerName}'. Known providers: {string.Join(", ", knownProviderNames)}." },
            });
        }

        var tenantExists = await dbContext.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken);
        if (!tenantExists)
        {
            return Results.NotFound(new { error = new { message = "Tenant not found." } });
        }

        await secretStore.SetSecretAsync(
            ProviderCredentialSecretName.For(tenantId, providerName), request.ApiKey, cancellationToken);

        return Results.NoContent();
    }

    /// <summary>Never returns the credential value itself — only whether one is configured per known provider.</summary>
    private static async Task<IResult> ListAsync(
        Guid tenantId,
        GatewayDbContext dbContext,
        ISecretStore secretStore,
        IEnumerable<IProviderClient> providerClients,
        CancellationToken cancellationToken)
    {
        var tenantExists = await dbContext.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken);
        if (!tenantExists)
        {
            return Results.NotFound(new { error = new { message = "Tenant not found." } });
        }

        var results = new List<object>();
        foreach (var providerName in providerClients.Select(c => c.ProviderName).OrderBy(n => n))
        {
            var secret = await secretStore.GetSecretAsync(
                ProviderCredentialSecretName.For(tenantId, providerName), cancellationToken);
            results.Add(new { provider = providerName, configured = secret is not null });
        }

        return Results.Ok(results);
    }
}
