using Core.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Core.Tests.Secrets;

public class LocalDevSecretStoreTests : IDisposable
{
    private readonly string _tempDirectory = Directory.CreateTempSubdirectory("gateway-secret-store-tests").FullName;

    private LocalDevSecretStore CreateStore() => new(
        new EphemeralDataProtectionProvider(),
        new LocalDevSecretStoreOptions { FilePath = Path.Combine(_tempDirectory, "secrets.json") });

    [Fact]
    public async Task Set_then_get_round_trips_the_value()
    {
        var store = CreateStore();

        await store.SetSecretAsync("tenant-a-provider-openai", "sk-test-123", CancellationToken.None);
        var value = await store.GetSecretAsync("tenant-a-provider-openai", CancellationToken.None);

        Assert.Equal("sk-test-123", value);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_secret()
    {
        var store = CreateStore();

        var value = await store.GetSecretAsync("does-not-exist", CancellationToken.None);

        Assert.Null(value);
    }

    [Fact]
    public async Task Delete_removes_the_secret()
    {
        var store = CreateStore();
        await store.SetSecretAsync("tenant-a-provider-openai", "sk-test-123", CancellationToken.None);

        await store.DeleteSecretAsync("tenant-a-provider-openai", CancellationToken.None);

        Assert.Null(await store.GetSecretAsync("tenant-a-provider-openai", CancellationToken.None));
    }

    [Fact]
    public async Task Values_are_not_stored_as_plaintext_on_disk()
    {
        var filePath = Path.Combine(_tempDirectory, "secrets.json");
        var store = new LocalDevSecretStore(
            new EphemeralDataProtectionProvider(),
            new LocalDevSecretStoreOptions { FilePath = filePath });

        await store.SetSecretAsync("tenant-a-provider-openai", "sk-super-secret-value", CancellationToken.None);

        var rawFileContents = await File.ReadAllTextAsync(filePath);
        Assert.DoesNotContain("sk-super-secret-value", rawFileContents);
    }

    [Fact]
    public async Task A_second_store_instance_sharing_the_same_file_and_key_ring_can_read_values_back()
    {
        var protectionProvider = new EphemeralDataProtectionProvider();
        var options = new LocalDevSecretStoreOptions { FilePath = Path.Combine(_tempDirectory, "secrets.json") };
        var firstStore = new LocalDevSecretStore(protectionProvider, options);
        await firstStore.SetSecretAsync("tenant-a-provider-openai", "sk-test-123", CancellationToken.None);

        var secondStore = new LocalDevSecretStore(protectionProvider, options);
        var value = await secondStore.GetSecretAsync("tenant-a-provider-openai", CancellationToken.None);

        Assert.Equal("sk-test-123", value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
