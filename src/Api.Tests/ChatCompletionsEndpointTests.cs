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
using Core.RateLimiting;
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

                services.RemoveAll<IRateLimitStore>();
                services.AddSingleton<IRateLimitStore, InMemoryRateLimitStore>();

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

    [Fact]
    public async Task Blocks_requests_once_the_tenants_token_quota_is_exceeded()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var tenant = await dbContext.Tenants.FirstAsync(t => t.Id == _tenantId);
            tenant.TokenQuotaPerWindow = 10;
            await dbContext.SaveChangesAsync();
        }

        _stubOpenAiClient.Response = new ProviderResponse(200, new JsonObject
        {
            ["id"] = "resp-1",
            ["usage"] = new JsonObject { ["prompt_tokens"] = 6, ["completion_tokens"] = 6 },
        });
        var client = CreateAuthenticatedClient();

        var first = await client.PostAsync(
            "/v1/chat/completions", new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));
        var second = await client.PostAsync(
            "/v1/chat/completions", new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
    }

    [Fact]
    public async Task Does_not_rate_limit_a_tenant_with_no_configured_quota()
    {
        _stubOpenAiClient.Response = new ProviderResponse(200, new JsonObject
        {
            ["id"] = "resp-1",
            ["usage"] = new JsonObject { ["prompt_tokens"] = 1_000_000, ["completion_tokens"] = 1_000_000 },
        });
        var client = CreateAuthenticatedClient();

        var first = await client.PostAsync(
            "/v1/chat/completions", new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));
        var second = await client.PostAsync(
            "/v1/chat/completions", new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Blocks_requests_once_the_api_keys_own_quota_is_exceeded_even_with_room_left_on_the_tenant()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var apiKey = await dbContext.ApiKeys.IgnoreQueryFilters().FirstAsync(k => k.TenantId == _tenantId);
            apiKey.TokenQuotaPerWindow = 10;
            var tenant = await dbContext.Tenants.FirstAsync(t => t.Id == _tenantId);
            tenant.TokenQuotaPerWindow = 1_000_000; // plenty of room at the tenant level
            await dbContext.SaveChangesAsync();
        }

        _stubOpenAiClient.Response = new ProviderResponse(200, new JsonObject
        {
            ["id"] = "resp-1",
            ["usage"] = new JsonObject { ["prompt_tokens"] = 6, ["completion_tokens"] = 6 },
        });
        var client = CreateAuthenticatedClient();

        var first = await client.PostAsync(
            "/v1/chat/completions", new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));
        var second = await client.PostAsync(
            "/v1/chat/completions", new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
    }

    [Fact]
    public async Task A_jwt_authenticated_request_is_not_subject_to_a_per_api_key_quota()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var apiKey = await dbContext.ApiKeys.IgnoreQueryFilters().FirstAsync(k => k.TenantId == _tenantId);
            apiKey.TokenQuotaPerWindow = 1; // tiny — would block the legacy-key path immediately
            await dbContext.SaveChangesAsync();
        }

        _stubOpenAiClient.Response = new ProviderResponse(200, new JsonObject
        {
            ["id"] = "resp-1",
            ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 100 },
        });
        var client = CreateJwtAuthenticatedClient(_tenantId);

        var response = await client.PostAsync(
            "/v1/chat/completions", new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<UsageEvent> GetLatestUsageEventAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        return await dbContext.UsageEvents.IgnoreQueryFilters().OrderByDescending(e => e.CreatedAtUtc).FirstAsync();
    }

    [Fact]
    public async Task Records_a_usage_event_for_a_successful_request()
    {
        _stubOpenAiClient.Response = new ProviderResponse(200, new JsonObject
        {
            ["id"] = "resp-1",
            ["usage"] = new JsonObject { ["prompt_tokens"] = 7, ["completion_tokens"] = 4 },
        });
        var client = CreateAuthenticatedClient();

        await client.PostAsync(
            "/v1/chat/completions", new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));

        var usageEvent = await GetLatestUsageEventAsync();
        Assert.Equal(_tenantId, usageEvent.TenantId);
        Assert.Equal("openai", usageEvent.Provider);
        Assert.Equal("gpt-4o-mini", usageEvent.Model);
        Assert.False(usageEvent.Streamed);
        Assert.Equal(200, usageEvent.StatusCode);
        Assert.Equal(7, usageEvent.PromptTokens);
        Assert.Equal(4, usageEvent.CompletionTokens);
        Assert.True(usageEvent.LatencyMs >= 0);
    }

    [Fact]
    public async Task Records_a_usage_event_for_a_streaming_request()
    {
        var client = CreateAuthenticatedClient();

        await client.PostAsync(
            "/v1/chat/completions", new StringContent("""{"model":"gpt-4o-mini","stream":true}""", Encoding.UTF8, "application/json"));

        var usageEvent = await GetLatestUsageEventAsync();
        Assert.True(usageEvent.Streamed);
        Assert.Equal(_stubOpenAiClient.StreamedUsage!.PromptTokens, usageEvent.PromptTokens);
        Assert.Equal(_stubOpenAiClient.StreamedUsage!.CompletionTokens, usageEvent.CompletionTokens);
    }

    [Fact]
    public async Task Records_a_zero_token_usage_event_when_rate_limited()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var tenant = await dbContext.Tenants.FirstAsync(t => t.Id == _tenantId);
            tenant.TokenQuotaPerWindow = 0;
            await dbContext.SaveChangesAsync();
        }
        var client = CreateAuthenticatedClient();

        await client.PostAsync(
            "/v1/chat/completions", new StringContent("""{"model":"gpt-4o-mini"}""", Encoding.UTF8, "application/json"));

        var usageEvent = await GetLatestUsageEventAsync();
        Assert.Equal(429, usageEvent.StatusCode);
        Assert.Equal(0, usageEvent.PromptTokens);
        Assert.Equal(0, usageEvent.CompletionTokens);
    }

    [Fact]
    public async Task Records_a_usage_event_when_no_provider_credential_is_configured()
    {
        var client = CreateAuthenticatedClient();

        await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"claude-3-5-sonnet-20241022"}""", Encoding.UTF8, "application/json"));

        var usageEvent = await GetLatestUsageEventAsync();
        Assert.Equal("anthropic", usageEvent.Provider);
        Assert.Equal(400, usageEvent.StatusCode);
    }
}
