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
}
