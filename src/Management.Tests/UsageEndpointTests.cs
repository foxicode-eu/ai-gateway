using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Core.Entities;
using Core.Persistence;
using Management.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Management.Tests;

public class UsageEndpointTests : IClassFixture<ManagementApiFactory>
{
    private readonly ManagementApiFactory _factory;
    private readonly HttpClient _client;

    public UsageEndpointTests(ManagementApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private async Task<Guid> CreateTenantAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/tenants", new TenantsEndpoint.CreateTenantRequest(name));
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        return body!["id"]!.GetValue<Guid>();
    }

    private async Task SeedUsageEventAsync(Guid tenantId, string provider, int promptTokens, int completionTokens, int statusCode, DateTimeOffset createdAtUtc)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        dbContext.UsageEvents.Add(new UsageEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Provider = provider,
            Model = "gpt-4o-mini",
            StatusCode = statusCode,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            LatencyMs = 100,
            CreatedAtUtc = createdAtUtc,
        });
        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task Returns_404_for_an_unknown_tenant()
    {
        var response = await _client.GetAsync($"/tenants/{Guid.NewGuid()}/usage");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Aggregates_usage_across_providers()
    {
        var tenantId = await CreateTenantAsync("Acme");
        var now = DateTimeOffset.UtcNow;
        await SeedUsageEventAsync(tenantId, "openai", 10, 5, 200, now);
        await SeedUsageEventAsync(tenantId, "openai", 20, 10, 200, now);
        await SeedUsageEventAsync(tenantId, "anthropic", 7, 3, 401, now);

        var response = await _client.GetAsync($"/tenants/{tenantId}/usage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(3, body?["totalRequests"]?.GetValue<int>());
        Assert.Equal(37, body?["totalPromptTokens"]?.GetValue<int>());
        Assert.Equal(18, body?["totalCompletionTokens"]?.GetValue<int>());
        Assert.Equal(55, body?["totalTokens"]?.GetValue<int>());
        Assert.Equal(1, body?["errorCount"]?.GetValue<int>());

        var byProvider = body?["byProvider"]?.AsArray();
        Assert.Equal(2, byProvider?.Count);
        var openai = byProvider!.First(p => p!["provider"]!.GetValue<string>() == "openai")!;
        Assert.Equal(2, openai["requests"]!.GetValue<int>());
        Assert.Equal(30, openai["promptTokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task Excludes_events_outside_the_requested_window()
    {
        var tenantId = await CreateTenantAsync("Acme");
        await SeedUsageEventAsync(tenantId, "openai", 100, 100, 200, DateTimeOffset.UtcNow.AddDays(-2));
        await SeedUsageEventAsync(tenantId, "openai", 10, 10, 200, DateTimeOffset.UtcNow);

        var response = await _client.GetAsync($"/tenants/{tenantId}/usage?sinceHours=24");

        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(1, body?["totalRequests"]?.GetValue<int>());
        Assert.Equal(10, body?["totalPromptTokens"]?.GetValue<int>());
    }

    [Fact]
    public async Task Returns_zeroed_totals_for_a_tenant_with_no_usage()
    {
        var tenantId = await CreateTenantAsync("Acme");

        var response = await _client.GetAsync($"/tenants/{tenantId}/usage");

        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(0, body?["totalRequests"]?.GetValue<int>());
        Assert.Empty(body!["byProvider"]!.AsArray());
    }

    [Fact]
    public async Task Rejects_requests_without_a_valid_admin_token()
    {
        var tenantId = await CreateTenantAsync("Acme");
        var unauthenticatedClient = _factory.CreateClient();

        var response = await unauthenticatedClient.GetAsync($"/tenants/{tenantId}/usage");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
