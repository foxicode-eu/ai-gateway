using System.Text.Json.Nodes;
using Api.Authentication;
using Api.RateLimiting;
using Core.Providers;
using Core.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Endpoints;

public static class ChatCompletionsEndpoint
{
    public static IEndpointRouteBuilder MapChatCompletions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", HandleAsync)
            .AddEndpointFilter<TenantAuthenticationFilter>();
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest httpRequest,
        CancellationToken cancellationToken)
    {
        JsonNode? requestNode;
        try
        {
            requestNode = await JsonNode.ParseAsync(httpRequest.Body, cancellationToken: cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.BadRequest(new { error = new { message = "Request body is not valid JSON." } });
        }

        if (requestNode is not JsonObject requestBody)
        {
            return Results.BadRequest(new { error = new { message = "Request body must be a JSON object." } });
        }

        if (requestBody["model"] is not JsonValue || requestBody["model"]!.GetValue<string>() is not { Length: > 0 } model)
        {
            return Results.BadRequest(new { error = new { message = "Request body must include a 'model' field." } });
        }

        var streamRequested = requestBody.TryGetPropertyValue("stream", out var streamNode)
            && streamNode is JsonValue streamValue
            && streamValue.TryGetValue<bool>(out var isStream)
            && isStream;

        // The auth filter set this before this handler ran.
        var authenticated = (AuthenticatedTenant)httpRequest.HttpContext.Items[nameof(AuthenticatedTenant)]!;
        var tenantId = authenticated.TenantId;

        var services = httpRequest.HttpContext.RequestServices;

        var rateLimitGate = services.GetRequiredService<RateLimitGate>();
        var rateLimitCheck = await rateLimitGate.CheckAsync(tenantId, authenticated.ApiKeyId, cancellationToken);
        if (!rateLimitCheck.IsAllowed)
        {
            return Results.Json(
                new { error = new { message = $"Token rate limit exceeded for this {rateLimitCheck.BlockedScope}." } },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var providerName = ProviderRouting.ResolveProviderName(model);
        var secretStore = services.GetRequiredService<ISecretStore>();
        var providerApiKey = await secretStore.GetSecretAsync(
            ProviderCredentialSecretName.For(tenantId, providerName), cancellationToken);

        if (providerApiKey is null)
        {
            return Results.Json(
                new { error = new { message = $"No '{providerName}' credential configured for this tenant." } },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var providerClient = services.GetRequiredService<IProviderClientRegistry>().Get(providerName);

        if (streamRequested)
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Api.Endpoints.ChatCompletionsEndpoint");
            var httpResponse = httpRequest.HttpContext.Response;

            return Results.Stream(
                async responseBody =>
                {
                    var writer = new HttpResponseStreamWriter(httpResponse);
                    var usage = await providerClient.StreamChatCompletionAsync(
                        requestBody, providerApiKey, writer, cancellationToken);

                    if (usage is not null)
                    {
                        logger.LogInformation(
                            "Streamed chat completion finished for tenant {TenantId} via {Provider}: {PromptTokens} prompt + {CompletionTokens} completion tokens",
                            tenantId, providerName, usage.PromptTokens, usage.CompletionTokens);

                        await rateLimitGate.RecordUsageAsync(
                            tenantId, authenticated.ApiKeyId, rateLimitCheck,
                            usage.PromptTokens + usage.CompletionTokens, cancellationToken);
                    }
                },
                contentType: "text/event-stream");
        }

        var providerResponse = await providerClient.CreateChatCompletionAsync(requestBody, providerApiKey, cancellationToken);

        var totalTokens = ExtractTotalTokens(providerResponse.Body);
        await rateLimitGate.RecordUsageAsync(tenantId, authenticated.ApiKeyId, rateLimitCheck, totalTokens, cancellationToken);

        return Results.Json(providerResponse.Body, statusCode: providerResponse.StatusCode);
    }

    private static int ExtractTotalTokens(JsonObject? body)
    {
        if (body?["usage"] is not JsonObject usage)
        {
            return 0;
        }

        var promptTokens = usage["prompt_tokens"]?.GetValue<int>() ?? 0;
        var completionTokens = usage["completion_tokens"]?.GetValue<int>() ?? 0;
        return promptTokens + completionTokens;
    }

    /// <summary>
    /// Adapts ASP.NET Core's <see cref="HttpResponse"/> to <see cref="IStreamResponseWriter"/> so <c>Core</c>
    /// doesn't need to depend on ASP.NET Core types. Only safe to mutate status code/content type through this
    /// before the first write to <see cref="Body"/> — see <see cref="IStreamResponseWriter"/>'s doc comment.
    /// </summary>
    private sealed class HttpResponseStreamWriter(HttpResponse response) : IStreamResponseWriter
    {
        public Stream Body => response.Body;

        public void SetStatusCode(int statusCode) => response.StatusCode = statusCode;

        public void SetContentType(string contentType) => response.ContentType = contentType;
    }
}
