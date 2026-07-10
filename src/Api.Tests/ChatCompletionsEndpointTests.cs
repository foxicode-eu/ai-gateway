using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Core.Auth;
using Core.Entities;
using Core.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Core.Providers;
using Core.Secrets;
using Core.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Api.Tests;

public class ChatCompletionsEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private sealed class StubProviderClient(string providerName) : IProviderClient
    {
        public JsonObject? LastRequest { get; private set; }
        public string? LastApiKey { get; private set; }
        public ProviderResponse Response { get; set; } = new(200, new JsonObject { ["id"] = "stub-response" });
        public string StreamedBody { get; set; } = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n";
        public StreamUsage? StreamedUsage { get; set; } = new(3, 2);
        public (int StatusCode, string Body)? StreamedError { get; set; }

        public string ProviderName => providerName;

        public Task<ProviderResponse> CreateChatCompletionAsync(JsonObject request, string apiKey, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastApiKey = apiKey;
            return Task.FromResult(Response);
        }

        public async Task<StreamUsage?> StreamChatCompletionAsync(
            JsonObject request, string apiKey, IStreamResponseWriter writer, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastApiKey = apiKey;

            if (StreamedError is { } error)
            {
                writer.SetStatusCode(error.StatusCode);
                writer.SetContentType("application/json");
                await writer.Body.WriteAsync(Encoding.UTF8.GetBytes(error.Body), cancellationToken);
                return null;
            }

            await writer.Body.WriteAsync(Encoding.UTF8.GetBytes(StreamedBody), cancellationToken);
            await writer.Body.FlushAsync(cancellationToken);
            return StreamedUsage;
        }
    }

    private sealed class StubSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public void Seed(string name, string value) => _values[name] = value;

        public Task SetSecretAsync(string name, string value, CancellationToken cancellationToken)
        {
            _values[name] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(_values.TryGetValue(name, out var value) ? value : null);

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken)
        {
            _values.Remove(name);
            return Task.CompletedTask;
        }
    }

    private readonly WebApplicationFactory<Program> _factory;
    private readonly StubProviderClient _stubOpenAiClient = new("openai");
    private readonly StubSecretStore _stubSecretStore = new();
    private readonly string _databaseName = Guid.NewGuid().ToString();

    private readonly AuthenticationOptions _authenticationOptions = new()
    {
        Mode = "StaticKey",
        Audience = "api-tests",
        StaticKey = new StaticKeyOptions
        {
            Issuer = "api-tests",
            SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        },
    };

    private Guid _tenantId;
    private string _apiKey = "";

    public ChatCompletionsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<GatewayDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<GatewayDbContext>>();
                services.AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase(_databaseName));

                services.RemoveAll<IProviderClient>();
                services.AddSingleton<IProviderClient>(_stubOpenAiClient);

                services.RemoveAll<ISecretStore>();
                services.AddSingleton<ISecretStore>(_stubSecretStore);

                var authOptions = _authenticationOptions;
                services.Configure<AuthenticationOptions>(o =>
                {
                    o.Mode = authOptions.Mode;
                    o.Audience = authOptions.Audience;
                    o.TenantIdClaimType = authOptions.TenantIdClaimType;
                    o.StaticKey = authOptions.StaticKey;
                });
            });
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAtUtc = DateTimeOffset.UtcNow };
        dbContext.Tenants.Add(tenant);
        _tenantId = tenant.Id;

        _apiKey = ApiKeyGenerator.GenerateSecret();
        dbContext.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "test-key",
            KeyHash = ApiKeyGenerator.Hash(_apiKey),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await dbContext.SaveChangesAsync();

        _stubSecretStore.Seed(ProviderCredentialSecretName.For(_tenantId, "openai"), "sk-tenant-openai-key");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient CreateAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return client;
    }

    private HttpClient CreateJwtAuthenticatedClient(Guid tenantId)
    {
        var client = _factory.CreateClient();
        var token = LocalDevTokenIssuer.IssueToken(_authenticationOptions, tenantId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Proxies_a_valid_request_to_the_resolved_providers_credential()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"gpt-4o-mini","messages":[]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("stub-response", body?["id"]?.GetValue<string>());
        Assert.Equal("gpt-4o-mini", _stubOpenAiClient.LastRequest?["model"]?.GetValue<string>());
        Assert.Equal("sk-tenant-openai-key", _stubOpenAiClient.LastApiKey);
    }

    [Fact]
    public async Task Propagates_a_non_200_status_code_from_the_provider()
    {
        _stubOpenAiClient.Response = new ProviderResponse(429, new JsonObject { ["error"] = "rate limited" });
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_requests_missing_the_model_field()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"messages":[]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_stubOpenAiClient.LastRequest);
    }

    [Fact]
    public async Task Streams_the_providers_sse_body_back_with_the_right_content_type()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"gpt-4o-mini","stream":true}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(_stubOpenAiClient.StreamedBody, body);
        Assert.True(_stubOpenAiClient.LastRequest?["stream"]?.GetValue<bool>());
        Assert.Equal("sk-tenant-openai-key", _stubOpenAiClient.LastApiKey);
    }

    [Fact]
    public async Task Rejects_a_streaming_request_when_the_tenant_has_no_credential_for_the_resolved_provider()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"claude-3-5-sonnet-20241022","stream":true}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Returns_a_json_error_instead_of_sse_when_the_provider_rejects_a_streaming_request()
    {
        _stubOpenAiClient.StreamedError = (401, """{"error":{"message":"Incorrect API key provided"}}""");
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"gpt-4o-mini","stream":true}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Incorrect API key provided", body?["error"]?["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task Rejects_malformed_json_bodies()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_requests_without_an_authorization_header()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_requests_with_an_unknown_api_key()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-gw-does-not-exist");

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_requests_when_the_tenant_has_no_credential_for_the_resolved_provider()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"claude-3-5-sonnet-20241022"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Authenticates_via_a_valid_jwt_carrying_the_tenant_id_claim()
    {
        var client = CreateJwtAuthenticatedClient(_tenantId);

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"gpt-4o-mini","messages":[]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("sk-tenant-openai-key", _stubOpenAiClient.LastApiKey);
    }

    [Fact]
    public async Task Rejects_a_jwt_whose_tenant_id_claim_does_not_match_a_known_tenant()
    {
        var client = CreateJwtAuthenticatedClient(Guid.NewGuid());

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_jwt_looking_credential_with_an_invalid_signature()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.validjwt");

        var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
