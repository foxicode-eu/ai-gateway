namespace Core.Secrets;

/// <summary>
/// Stores tenant-supplied provider credentials (BYOK). Production uses Azure Key Vault
/// (<see cref="AzureKeyVaultSecretStore"/>); local development uses <see cref="LocalDevSecretStore"/> — see its
/// doc comment for why that one must never be used in production.
/// </summary>
public interface ISecretStore
{
    Task SetSecretAsync(string name, string value, CancellationToken cancellationToken);

    /// <returns>The secret value, or null if no secret exists with that name.</returns>
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken);

    Task DeleteSecretAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Naming convention for the secret that holds a tenant's credential for a given provider.</summary>
public static class ProviderCredentialSecretName
{
    public static string For(Guid tenantId, string providerName) => $"tenant-{tenantId:N}-provider-{providerName.ToLowerInvariant()}";
}
