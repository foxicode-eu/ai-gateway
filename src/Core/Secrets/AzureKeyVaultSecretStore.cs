using Azure;
using Azure.Security.KeyVault.Secrets;

namespace Core.Secrets;

/// <summary>Production <see cref="ISecretStore"/> backed by Azure Key Vault.</summary>
public sealed class AzureKeyVaultSecretStore(SecretClient secretClient) : ISecretStore
{
    public async Task SetSecretAsync(string name, string value, CancellationToken cancellationToken)
    {
        await secretClient.SetSecretAsync(name, value, cancellationToken);
    }

    public async Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var secret = await secretClient.GetSecretAsync(name, cancellationToken: cancellationToken);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteSecretAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await secretClient.StartDeleteSecretAsync(name, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone — deleting is idempotent.
        }
    }
}
