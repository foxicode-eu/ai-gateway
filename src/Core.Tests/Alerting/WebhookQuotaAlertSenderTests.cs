using System.Net;
using System.Text;
using System.Text.Json;
using Core.Alerting;
using Xunit;

namespace Core.Tests.Alerting;

public class WebhookQuotaAlertSenderTests
{
    private sealed class FakeHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode) { Content = new StringContent("", Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Posts_the_payload_as_json_to_the_given_webhook_url()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var sender = new WebhookQuotaAlertSender(new HttpClient(handler));
        var payload = new QuotaAlertPayload(Guid.NewGuid(), 80, 83.5, 1000, 835, DateTimeOffset.UtcNow);

        await sender.SendAsync("https://example.com/hooks/quota", payload, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://example.com/hooks/quota", handler.LastRequest.RequestUri!.ToString());
        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal(payload.TenantId, body.GetProperty("tenantId").GetGuid());
        Assert.Equal(80, body.GetProperty("thresholdPercentage").GetInt32());
    }

    [Fact]
    public async Task Throws_when_the_webhook_endpoint_returns_a_non_success_status()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError);
        var sender = new WebhookQuotaAlertSender(new HttpClient(handler));
        var payload = new QuotaAlertPayload(Guid.NewGuid(), 80, 83.5, 1000, 835, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => sender.SendAsync("https://example.com/hooks/quota", payload, CancellationToken.None));
    }
}
