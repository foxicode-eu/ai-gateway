using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Core.Auth;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddManagedIdentityAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<AuthenticationOptions>()
            .Bind(configuration.GetSection(AuthenticationOptions.ConfigurationSection));

        services.AddSingleton<IJwtAccessTokenValidator, JwtAccessTokenValidator>();

        return services;
    }
}
