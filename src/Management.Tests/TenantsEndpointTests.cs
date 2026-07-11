using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Management.Endpoints;
using Xunit;

namespace Management.Tests;

public class TenantsEndpointTests : IClassFixture<ManagementApiFactory>
{
    private readonly ManagementApiFactory _factory;
    private readonly HttpClient _client;

    public TenantsEndpointTests(ManagementApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task Creates_a_tenant_and_can_read_it_back()
    {
        var createResponse = await _client.PostAsJsonAsync("/tenants", new TenantsEndpoint.CreateTenantRequest("Acme"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonObject>();
        var tenantId = created?["id"]?.GetValue<Guid>();
        Assert.NotNull(tenantId);
        Assert.Equal("Acme", created?["name"]?.GetValue<string>());

        var getResponse = await _client.GetAsync($"/tenants/{tenantId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Acme", fetched?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task Rejects_a_blank_tenant_name()
    {
        var response = await _client.PostAsJsonAsync("/tenants", new TenantsEndpoint.CreateTenantRequest("  "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Returns_404_for_an_unknown_tenant()
    {
        var response = await _client.GetAsync($"/tenants/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_requests_without_a_valid_admin_token()
    {
        var unauthenticatedClient = _factory.CreateClient();

        var response = await unauthenticatedClient.PostAsJsonAsync("/tenants", new TenantsEndpoint.CreateTenantRequest("Acme"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Creates_a_tenant_with_a_token_quota()
    {
        var response = await _client.PostAsJsonAsync("/tenants", new TenantsEndpoint.CreateTenantRequest("Acme", TokenQuotaPerWindow: 5000));

        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(5000, body?["tokenQuotaPerWindow"]?.GetValue<int>());
    }

    [Fact]
    public async Task Rejects_a_negative_quota_on_create()
    {
        var response = await _client.PostAsJsonAsync("/tenants", new TenantsEndpoint.CreateTenantRequest("Acme", TokenQuotaPerWindow: -1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Updates_a_tenants_quota()
    {
        var createResponse = await _client.PostAsJsonAsync("/tenants", new TenantsEndpoint.CreateTenantRequest("Acme"));
        var tenantId = (await createResponse.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();

        var patchResponse = await _client.PatchAsJsonAsync($"/tenants/{tenantId}", new TenantsEndpoint.UpdateTenantRequest(2500));

        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        var updated = await patchResponse.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(2500, updated?["tokenQuotaPerWindow"]?.GetValue<int>());

        var getResponse = await _client.GetAsync($"/tenants/{tenantId}");
        var fetched = await getResponse.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(2500, fetched?["tokenQuotaPerWindow"]?.GetValue<int>());
    }

    [Fact]
    public async Task Can_clear_a_tenants_quota_back_to_unlimited()
    {
        var createResponse = await _client.PostAsJsonAsync(
            "/tenants", new TenantsEndpoint.CreateTenantRequest("Acme", TokenQuotaPerWindow: 5000));
        var tenantId = (await createResponse.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();

        var patchResponse = await _client.PatchAsJsonAsync($"/tenants/{tenantId}", new TenantsEndpoint.UpdateTenantRequest(null));

        var updated = await patchResponse.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Null(updated?["tokenQuotaPerWindow"]);
    }

    [Fact]
    public async Task Returns_404_when_updating_an_unknown_tenant()
    {
        var response = await _client.PatchAsJsonAsync($"/tenants/{Guid.NewGuid()}", new TenantsEndpoint.UpdateTenantRequest(1000));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Lists_tenants_ordered_by_name()
    {
        await _client.PostAsJsonAsync("/tenants", new TenantsEndpoint.CreateTenantRequest("Zebra Corp"));
        await _client.PostAsJsonAsync("/tenants", new TenantsEndpoint.CreateTenantRequest("Acme Corp"));

        var response = await _client.GetAsync("/tenants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tenants = await response.Content.ReadFromJsonAsync<JsonArray>();
        var names = tenants!.Select(t => t!["name"]!.GetValue<string>()).ToList();
        var acmeIndex = names.IndexOf("Acme Corp");
        var zebraIndex = names.IndexOf("Zebra Corp");
        Assert.True(acmeIndex >= 0 && zebraIndex >= 0 && acmeIndex < zebraIndex);
    }
}
