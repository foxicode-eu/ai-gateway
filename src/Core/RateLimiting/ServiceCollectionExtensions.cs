using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Core.RateLimiting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.ConfigurationSection));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITokenRateLimiter, TokenRateLimiter>();

        var store = configuration[$"{RateLimitingOptions.ConfigurationSection}:Store"];
        switch (store)
        {
            case "Redis":
                var connectionString = configuration[$"{RateLimitingOptions.ConfigurationSection}:RedisConnectionString"]
                    ?? throw new InvalidOperationException(
                        $"Missing '{RateLimitingOptions.ConfigurationSection}:RedisConnectionString' configuration.");
                services.AddSingleton<IConnectionMultiplexer>(
                    _ => ConnectionMultiplexer.Connect(connectionString));
                services.AddSingleton<IRateLimitStore, RedisRateLimitStore>();
                break;

            case "InMemory":
                services.AddSingleton<IRateLimitStore, InMemoryRateLimitStore>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Missing or unrecognized '{RateLimitingOptions.ConfigurationSection}:Store' configuration (expected \"Redis\" or \"InMemory\", got \"{store}\").");
        }

        return services;
    }
}
