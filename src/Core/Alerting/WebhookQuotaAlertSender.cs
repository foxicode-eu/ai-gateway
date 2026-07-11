using System.Net.Http.Json;

namespace Core.Alerting;

/// <summary>POSTs <see cref="QuotaAlertPayload"/> as JSON to a tenant-configured webhook URL.</summary>
public sealed class WebhookQuotaAlertSender(HttpClient httpClient) : IQuotaAlertSender
{
    public async Task SendAsync(string webhookUrl, QuotaAlertPayload payload, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(webhookUrl, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
