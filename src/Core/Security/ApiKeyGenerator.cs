using System.Security.Cryptography;

namespace Core.Security;

/// <summary>Generates and hashes tenant-facing API keys. The plaintext value is only ever shown once, at issuance.</summary>
public static class ApiKeyGenerator
{
    private const string Prefix = "sk-gw-";

    /// <summary>Creates a new random plaintext key. Store only its hash (<see cref="Hash"/>); never persist this value.</summary>
    public static string GenerateSecret() => Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>One-way hash suitable for looking up an API key by its plaintext value without storing the plaintext.</summary>
    public static string Hash(string plaintextKey) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintextKey))).ToLowerInvariant();
}
