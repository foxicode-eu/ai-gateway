using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Core.Sessions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGatewaySessions(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<GatewaySessionOptions>()
            .Bind(configuration.GetSection(GatewaySessionOptions.ConfigurationSection));

        var store = configuration[$"{GatewaySessionOptions.ConfigurationSection}:Store"];
        switch (store)
        {
            case "Redis":
                var connectionString = configuration[$"{GatewaySessionOptions.ConfigurationSection}:RedisConnectionString"]
                    ?? throw new InvalidOperationException(
                        $"Missing '{GatewaySessionOptions.ConfigurationSection}:RedisConnectionString' configuration.");
                services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connectionString));
                services.AddSingleton<ISessionStore, RedisSessionStore>();
                break;

            case "InMemory":
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<ISessionStore, InMemorySessionStore>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Missing or unrecognized '{GatewaySessionOptions.ConfigurationSection}:Store' configuration (expected \"Redis\" or \"InMemory\", got \"{store}\").");
        }

        return services;
    }
}
