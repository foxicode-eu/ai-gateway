using System.Reflection;
using Core.Providers;
using Core.Providers.Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Core.Tests.Providers;

public class ServiceCollectionExtensionsTests
{
    // Regression test: AddOpenAiProviderClient and AddAnthropicProviderClient previously both registered their
    // typed HttpClient as `AddHttpClient<IProviderClient, TImpl>`, which keys the underlying named HttpClient
    // by TClient (IProviderClient) rather than TImplementation — so both providers' BaseAddress-configuring
    // delegates collided under the same name, and whichever was registered last silently won for *both*
    // providers. Caught live: an OpenAI-routed request came back with an Anthropic-shaped error because
    // OpenAiProviderClient's HttpClient had BaseAddress https://api.anthropic.com/.
    [Fact]
    public void OpenAi_and_Anthropic_clients_get_their_own_distinct_base_addresses_when_registered_together()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        services.AddProviderClients(configuration);
        var provider = services.BuildServiceProvider();

        var openAiClient = provider.GetRequiredService<OpenAiProviderClient>();
        var anthropicClient = provider.GetRequiredService<AnthropicProviderClient>();

        Assert.Equal("https://api.openai.com/", GetHttpClient(openAiClient).BaseAddress?.ToString());
        Assert.Equal("https://api.anthropic.com/", GetHttpClient(anthropicClient).BaseAddress?.ToString());

        var registry = provider.GetRequiredService<IProviderClientRegistry>();
        Assert.IsType<OpenAiProviderClient>(registry.Get("openai"));
        Assert.IsType<AnthropicProviderClient>(registry.Get("anthropic"));
    }

    private static HttpClient GetHttpClient(object providerClient)
    {
        var field = providerClient.GetType()
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(f => f.FieldType == typeof(HttpClient))
            ?? throw new InvalidOperationException(
                $"Expected a captured HttpClient field on {providerClient.GetType().Name} (primary constructor capture).");

        return (HttpClient)field.GetValue(providerClient)!;
    }
}
