namespace Core.Alerting;

/// <summary>Delivers a quota-threshold alert to a tenant-configured destination. Currently webhook-only — see
/// <see cref="WebhookQuotaAlertSender"/> — email delivery is an open question (`ARCHITECTURE.md`).</summary>
public interface IQuotaAlertSender
{
    /// <summary>
    /// Throws if delivery fails (non-2xx response, network error) — callers decide how to handle that (e.g. log
    /// and swallow, so a webhook failure never breaks the chat-completion request path it was triggered from).
    /// </summary>
    Task SendAsync(string webhookUrl, QuotaAlertPayload payload, CancellationToken cancellationToken);
}
