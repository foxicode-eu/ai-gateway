using Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Core.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<ICurrentTenantAccessor, AsyncLocalCurrentTenantAccessor>();
        services.AddDbContext<GatewayDbContext>(options => options.UseNpgsql(connectionString));

        return services;
    }
}
