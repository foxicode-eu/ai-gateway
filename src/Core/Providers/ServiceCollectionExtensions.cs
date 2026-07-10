using Core.Providers.Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Core.Providers;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the OpenAI and Anthropic provider clients plus <see cref="IProviderClientRegistry"/>.</summary>
    public static IServiceCollection AddProviderClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOpenAiProviderClient(configuration);
        services.AddAnthropicProviderClient(configuration);
        services.AddSingleton<IProviderClientRegistry, ProviderClientRegistry>();

        return services;
    }

    public static IServiceCollection AddOpenAiProviderClient(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<OpenAiProviderOptions>()
            .Bind(configuration.GetSection(OpenAiProviderOptions.ConfigurationSection));

        // Typed-client registration is keyed by TClient's type name, not TImplementation's. Registering this
        // (and AnthropicProviderClient below) as `AddHttpClient<IProviderClient, TImpl>` would have both
        // register their BaseAddress-configuring delegate under the same named client ("IProviderClient"),
        // and HttpClientFactory applies every delegate registered for a name to any client built under it —
        // so the two providers would stomp on each other's BaseAddress. Keying by the concrete type keeps
        // them isolated; IProviderClient is bridged separately below.
        services.AddHttpClient<OpenAiProviderClient>((serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<OpenAiProviderOptions>>().Value;
            httpClient.BaseAddress = new Uri(options.BaseUrl);
        });
        services.AddTransient<IProviderClient>(sp => sp.GetRequiredService<OpenAiProviderClient>());

        return services;
    }

    public static IServiceCollection AddAnthropicProviderClient(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<AnthropicProviderOptions>()
            .Bind(configuration.GetSection(AnthropicProviderOptions.ConfigurationSection));

        services.AddHttpClient<AnthropicProviderClient>((serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AnthropicProviderOptions>>().Value;
            httpClient.BaseAddress = new Uri(options.BaseUrl);
        });
        services.AddTransient<IProviderClient>(sp => sp.GetRequiredService<AnthropicProviderClient>());

        return services;
    }
}
