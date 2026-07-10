using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Core.Providers;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenAiProviderClient(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<OpenAiProviderOptions>()
            .Bind(configuration.GetSection(OpenAiProviderOptions.ConfigurationSection))
            .ValidateDataAnnotations();

        services.AddHttpClient<IProviderClient, OpenAiProviderClient>((serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<OpenAiProviderOptions>>().Value;
            httpClient.BaseAddress = new Uri(options.BaseUrl);
        });

        return services;
    }
}
