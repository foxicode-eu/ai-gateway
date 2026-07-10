using Microsoft.Extensions.DependencyInjection;

namespace Core.Security;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiKeyAuthentication(this IServiceCollection services)
    {
        services.AddScoped<IApiKeyAuthenticator, ApiKeyAuthenticator>();
        return services;
    }
}
