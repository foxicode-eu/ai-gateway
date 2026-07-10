using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Management.Endpoints;
using Xunit;

namespace Management.Tests;

public class TenantsEndpointTests : IClassFixture<ManagementApiFactory>
{
    private readonly HttpClient _client;

    public TenantsEndpointTests(ManagementApiFactory factory)
    {
        _client = factory.CreateClient();
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
}
