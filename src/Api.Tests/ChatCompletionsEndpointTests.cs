using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Core.Providers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Api.Tests;

public class ChatCompletionsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class StubProviderClient : IProviderClient
    {
        public JsonObject? LastRequest { get; private set; }
        public ProviderResponse Response { get; set; } = new(200, new JsonObject { ["id"] = "stub-response" });

        public string ProviderName => "stub";

        public Task<ProviderResponse> CreateChatCompletionAsync(JsonObject request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }

    private readonly WebApplicationFactory<Program> _factory;
    private readonly StubProviderClient _stubProviderClient = new();

    public ChatCompletionsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IProviderClient>();
                services.AddSingleton<IProviderClient>(_stubProviderClient);
            });
        });
    }

    [Fact]
    public async Task Proxies_a_valid_request_to_the_provider_and_returns_its_response()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"gpt-4o-mini","messages":[]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("stub-response", body?["id"]?.GetValue<string>());
        Assert.Equal("gpt-4o-mini", _stubProviderClient.LastRequest?["model"]?.GetValue<string>());
    }

    [Fact]
    public async Task Propagates_a_non_200_status_code_from_the_provider()
    {
        _stubProviderClient.Response = new ProviderResponse(429, new JsonObject { ["error"] = "rate limited" });
        var client = _factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_requests_missing_the_model_field()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"messages":[]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_stubProviderClient.LastRequest);
    }

    [Fact]
    public async Task Rejects_streaming_requests_as_not_yet_supported()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"gpt-4o-mini","stream":true}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_stubProviderClient.LastRequest);
    }

    [Fact]
    public async Task Rejects_malformed_json_bodies()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
