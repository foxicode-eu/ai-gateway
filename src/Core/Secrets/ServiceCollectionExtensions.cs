using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Core.Secrets;

public static class ServiceCollectionExtensions
{
    private const string ProviderConfigKey = "Secrets:Provider";

    /// <summary>
    /// Registers <see cref="ISecretStore"/>, selected by the <c>Secrets:Provider</c> config value:
    /// "AzureKeyVault" (requires <c>Secrets:AzureKeyVault:VaultUri</c>) or "LocalDev" (see
    /// <see cref="LocalDevSecretStore"/> — local development only, never production).
    /// </summary>
    public static IServiceCollection AddGatewaySecrets(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration[ProviderConfigKey];

        switch (provider)
        {
            case "AzureKeyVault":
                var vaultUri = configuration["Secrets:AzureKeyVault:VaultUri"]
                    ?? throw new InvalidOperationException("Missing 'Secrets:AzureKeyVault:VaultUri' configuration.");
                services.AddSingleton(new SecretClient(new Uri(vaultUri), new DefaultAzureCredential()));
                services.AddSingleton<ISecretStore, AzureKeyVaultSecretStore>();
                break;

            case "LocalDev":
                var secretsFilePath = configuration[$"{LocalDevSecretStoreOptions.ConfigurationSection}:FilePath"]
                    ?? new LocalDevSecretStoreOptions().FilePath;
                var keyRingDirectory = Path.Combine(Path.GetDirectoryName(secretsFilePath) ?? ".", "keys");

                // Api and Management run as separate processes but must decrypt secrets the other one wrote, so
                // they need to share one key ring (fixed application name + a common directory on disk) instead
                // of each getting its own default, process-isolated one.
                services.AddDataProtection()
                    .SetApplicationName("ai-gateway-local-dev")
                    .PersistKeysToFileSystem(new DirectoryInfo(keyRingDirectory));

                services
                    .AddOptions<LocalDevSecretStoreOptions>()
                    .Bind(configuration.GetSection(LocalDevSecretStoreOptions.ConfigurationSection));
                services.AddSingleton<ISecretStore>(sp => new LocalDevSecretStore(
                    sp.GetRequiredService<IDataProtectionProvider>(),
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalDevSecretStoreOptions>>().Value));
                break;

            default:
                throw new InvalidOperationException(
                    $"Missing or unrecognized '{ProviderConfigKey}' configuration (expected \"AzureKeyVault\" or \"LocalDev\", got \"{provider}\").");
        }

        return services;
    }
}
