using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Core.Secrets;
using Management.Endpoints;
using Xunit;

namespace Management.Tests;

public class ProviderCredentialsEndpointTests : IClassFixture<ManagementApiFactory>
{
    private readonly ManagementApiFactory _factory;
    private readonly HttpClient _client;

    public ProviderCredentialsEndpointTests(ManagementApiFactory factory)
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

    [Fact]
    public async Task Stores_the_credential_in_the_secret_store()
    {
        var tenantId = await CreateTenantAsync("Acme");

        var response = await _client.PutAsJsonAsync(
            $"/tenants/{tenantId}/providers/openai", new ProviderCredentialsEndpoint.SetProviderCredentialRequest("sk-openai-test"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await _factory.SecretStore.GetSecretAsync(
            ProviderCredentialSecretName.For(tenantId, "openai"), CancellationToken.None);
        Assert.Equal("sk-openai-test", stored);
    }

    [Fact]
    public async Task Rejects_an_unknown_provider_name()
    {
        var tenantId = await CreateTenantAsync("Acme");

        var response = await _client.PutAsJsonAsync(
            $"/tenants/{tenantId}/providers/not-a-real-provider",
            new ProviderCredentialsEndpoint.SetProviderCredentialRequest("sk-test"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Returns_404_for_an_unknown_tenant()
    {
        var response = await _client.PutAsJsonAsync(
            $"/tenants/{Guid.NewGuid()}/providers/openai",
            new ProviderCredentialsEndpoint.SetProviderCredentialRequest("sk-test"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_blank_credential_value()
    {
        var tenantId = await CreateTenantAsync("Acme");

        var response = await _client.PutAsJsonAsync(
            $"/tenants/{tenantId}/providers/openai", new ProviderCredentialsEndpoint.SetProviderCredentialRequest(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Lists_known_providers_with_configured_status_but_never_the_secret_value()
    {
        var tenantId = await CreateTenantAsync("Acme");
        await _client.PutAsJsonAsync(
            $"/tenants/{tenantId}/providers/openai", new ProviderCredentialsEndpoint.SetProviderCredentialRequest("sk-openai-test"));

        var response = await _client.GetAsync($"/tenants/{tenantId}/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var providers = await response.Content.ReadFromJsonAsync<JsonArray>();
        Assert.Contains(providers!, p => p!["provider"]!.GetValue<string>() == "openai" && p["configured"]!.GetValue<bool>());
        Assert.Contains(providers!, p => p!["provider"]!.GetValue<string>() == "anthropic" && !p["configured"]!.GetValue<bool>());
        Assert.All(providers!, p => Assert.Null(p!["apiKey"]));
    }

    [Fact]
    public async Task Returns_404_when_listing_providers_for_an_unknown_tenant()
    {
        var response = await _client.GetAsync($"/tenants/{Guid.NewGuid()}/providers");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
