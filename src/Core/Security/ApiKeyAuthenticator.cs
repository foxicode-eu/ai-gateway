using Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Core.Security;

public sealed record ApiKeyAuthenticationResult(Guid TenantId, Guid ApiKeyId);

public interface IApiKeyAuthenticator
{
    /// <returns>Null if the key doesn't exist or has been revoked.</returns>
    Task<ApiKeyAuthenticationResult?> AuthenticateAsync(string plaintextKey, CancellationToken cancellationToken);
}

public sealed class ApiKeyAuthenticator(GatewayDbContext dbContext) : IApiKeyAuthenticator
{
    public async Task<ApiKeyAuthenticationResult?> AuthenticateAsync(string plaintextKey, CancellationToken cancellationToken)
    {
        var hash = ApiKeyGenerator.Hash(plaintextKey);

        // Bootstrapping lookup: we don't know the tenant yet — that's what we're resolving — so this
        // deliberately bypasses the tenant query filter for this one query rather than touching the ambient
        // scope. Once resolved, the caller sets the scope for the rest of the request.
        var apiKey = await dbContext.ApiKeys
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.KeyHash == hash, cancellationToken);

        if (apiKey is null || !apiKey.IsActive)
        {
            return null;
        }

        return new ApiKeyAuthenticationResult(apiKey.TenantId, apiKey.Id);
    }
}
