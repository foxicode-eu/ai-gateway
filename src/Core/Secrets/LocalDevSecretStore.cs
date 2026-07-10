using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Core.Secrets;

/// <summary>
/// Local-development-only <see cref="ISecretStore"/>: values are encrypted at rest with ASP.NET Core Data
/// Protection and stored in a single JSON file on disk. This exists so the tenant-onboarding → BYOK → proxy
/// flow can be exercised end-to-end without a real Azure Key Vault instance.
///
/// <b>Never use this in production.</b> The Data Protection key ring here is the default machine-local one —
/// it is not tenant-isolated, not rotated deliberately, and not suitable for secrets that matter. Production
/// must use <see cref="AzureKeyVaultSecretStore"/>.
/// </summary>
public sealed class LocalDevSecretStore : ISecretStore
{
    private readonly string _filePath;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public LocalDevSecretStore(IDataProtectionProvider dataProtectionProvider, LocalDevSecretStoreOptions options)
    {
        _filePath = options.FilePath;
        _protector = dataProtectionProvider.CreateProtector("Core.Secrets.LocalDevSecretStore");
    }

    public async Task SetSecretAsync(string name, string value, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var secrets = await ReadAllAsync(cancellationToken);
            secrets[name] = _protector.Protect(value);
            await WriteAllAsync(secrets, cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var secrets = await ReadAllAsync(cancellationToken);
            return secrets.TryGetValue(name, out var protectedValue) ? _protector.Unprotect(protectedValue) : null;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task DeleteSecretAsync(string name, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var secrets = await ReadAllAsync(cancellationToken);
            if (secrets.Remove(name))
            {
                await WriteAllAsync(secrets, cancellationToken);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken)
            ?? [];
    }

    private async Task WriteAllAsync(Dictionary<string, string> secrets, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, secrets, cancellationToken: cancellationToken);
    }
}

public sealed class LocalDevSecretStoreOptions
{
    public const string ConfigurationSection = "Secrets:LocalDev";

    public string FilePath { get; set; } = ".local/secrets.dev.json";
}
