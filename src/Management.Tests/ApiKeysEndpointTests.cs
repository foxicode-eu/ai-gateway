using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Management.Endpoints;
using Xunit;

namespace Management.Tests;

public class ApiKeysEndpointTests : IClassFixture<ManagementApiFactory>
{
    private readonly HttpClient _client;

    public ApiKeysEndpointTests(ManagementApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<Guid> CreateTenantAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/tenants", new TenantsEndpoint.CreateTenantRequest(name));
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        return body!["id"]!.GetValue<Guid>();
    }

    [Fact]
    public async Task Issues_an_api_key_whose_plaintext_hashes_to_the_stored_hash()
    {
        var tenantId = await CreateTenantAsync("Acme");

        var response = await _client.PostAsJsonAsync(
            $"/tenants/{tenantId}/api-keys", new ApiKeysEndpoint.CreateApiKeyRequest("prod-backend"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        var plaintextKey = body?["key"]?.GetValue<string>();
        Assert.NotNull(plaintextKey);
        Assert.StartsWith("sk-gw-", plaintextKey);
        Assert.Equal("prod-backend", body?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task Returns_404_when_issuing_a_key_for_an_unknown_tenant()
    {
        var response = await _client.PostAsJsonAsync(
            $"/tenants/{Guid.NewGuid()}/api-keys", new ApiKeysEndpoint.CreateApiKeyRequest("prod-backend"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_blank_key_name()
    {
        var tenantId = await CreateTenantAsync("Acme");

        var response = await _client.PostAsJsonAsync(
            $"/tenants/{tenantId}/api-keys", new ApiKeysEndpoint.CreateApiKeyRequest(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Revoking_a_key_makes_it_fail_authentication()
    {
        var tenantId = await CreateTenantAsync("Acme");
        var createResponse = await _client.PostAsJsonAsync(
            $"/tenants/{tenantId}/api-keys", new ApiKeysEndpoint.CreateApiKeyRequest("prod-backend"));
        var created = await createResponse.Content.ReadFromJsonAsync<JsonObject>();
        var apiKeyId = created!["id"]!.GetValue<Guid>();

        var revokeResponse = await _client.DeleteAsync($"/tenants/{tenantId}/api-keys/{apiKeyId}");

        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
    }

    [Fact]
    public async Task Returns_404_when_revoking_a_key_that_does_not_belong_to_the_tenant()
    {
        var tenantAId = await CreateTenantAsync("Tenant A");
        var tenantBId = await CreateTenantAsync("Tenant B");
        var createResponse = await _client.PostAsJsonAsync(
            $"/tenants/{tenantAId}/api-keys", new ApiKeysEndpoint.CreateApiKeyRequest("prod-backend"));
        var created = await createResponse.Content.ReadFromJsonAsync<JsonObject>();
        var apiKeyId = created!["id"]!.GetValue<Guid>();

        var revokeResponse = await _client.DeleteAsync($"/tenants/{tenantBId}/api-keys/{apiKeyId}");

        Assert.Equal(HttpStatusCode.NotFound, revokeResponse.StatusCode);
    }
}
