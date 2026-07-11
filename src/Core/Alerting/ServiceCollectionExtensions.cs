using Microsoft.Extensions.DependencyInjection;

namespace Core.Alerting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayAlerting(this IServiceCollection services)
    {
        services.AddHttpClient<WebhookQuotaAlertSender>();
        services.AddTransient<IQuotaAlertSender>(sp => sp.GetRequiredService<WebhookQuotaAlertSender>());
        return services;
    }
}
