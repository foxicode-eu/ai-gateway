using Core.Security;
using Xunit;

namespace Core.Tests.Security;

public class ApiKeyGeneratorTests
{
    [Fact]
    public void GenerateSecret_produces_unique_values_with_the_expected_prefix()
    {
        var first = ApiKeyGenerator.GenerateSecret();
        var second = ApiKeyGenerator.GenerateSecret();

        Assert.StartsWith("sk-gw-", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Hash_is_deterministic_for_the_same_input()
    {
        var secret = ApiKeyGenerator.GenerateSecret();

        Assert.Equal(ApiKeyGenerator.Hash(secret), ApiKeyGenerator.Hash(secret));
    }

    [Fact]
    public void Hash_differs_for_different_inputs()
    {
        var first = ApiKeyGenerator.GenerateSecret();
        var second = ApiKeyGenerator.GenerateSecret();

        Assert.NotEqual(ApiKeyGenerator.Hash(first), ApiKeyGenerator.Hash(second));
    }

    [Fact]
    public void Hash_does_not_return_the_plaintext_value()
    {
        var secret = ApiKeyGenerator.GenerateSecret();

        Assert.NotEqual(secret, ApiKeyGenerator.Hash(secret));
    }
}
