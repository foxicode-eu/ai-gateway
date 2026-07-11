using System.Diagnostics;
using System.Text.Json.Nodes;
using Api.Alerting;
using Api.Authentication;
using Api.Observability;
using Api.RateLimiting;
using Core.Observability;
using Core.Providers;
using Core.RateLimiting;
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

        var providerName = ProviderRouting.ResolveProviderName(model);

        // The auth filter set this before this handler ran.
        var authenticated = (AuthenticatedTenant)httpRequest.HttpContext.Items[nameof(AuthenticatedTenant)]!;
        var tenantId = authenticated.TenantId;

        var services = httpRequest.HttpContext.RequestServices;
        var rateLimitGate = services.GetRequiredService<RateLimitGate>();
        var quotaAlertGate = services.GetRequiredService<QuotaAlertGate>();
        var usageRecorder = services.GetRequiredService<UsageEventRecorder>();

        var stopwatch = Stopwatch.StartNew();
        using var activity = GatewayDiagnostics.ActivitySource.StartActivity("chat.completion");
        activity?.SetTag("gateway.tenant_id", tenantId);
        activity?.SetTag("gateway.provider", providerName);
        activity?.SetTag("gateway.model", model);
        activity?.SetTag("gateway.streamed", streamRequested);

        var rateLimitCheck = await rateLimitGate.CheckAsync(tenantId, authenticated.ApiKeyId, cancellationToken);

        // Records metrics + a usage event for every exit point below, then returns `result` unchanged — keeps
        // the three possible outcomes (rate-limited, no credential, completed) from each duplicating this.
        async Task<IResult> FinishAsync(IResult result, int statusCode, int promptTokens, int completionTokens)
        {
            stopwatch.Stop();
            var latencyMs = (long)stopwatch.Elapsed.TotalMilliseconds;

            activity?.SetTag("gateway.status_code", statusCode);
            activity?.SetStatus(statusCode >= 400 ? ActivityStatusCode.Error : ActivityStatusCode.Ok);

            var tags = new TagList
            {
                { "tenant_id", tenantId.ToString() },
                { "provider", providerName },
                { "model", model },
                { "status_code", statusCode },
            };
            GatewayDiagnostics.RequestCount.Add(1, tags);
            GatewayDiagnostics.RequestDuration.Record(latencyMs, tags);

            if (promptTokens > 0)
            {
                GatewayDiagnostics.TokensUsed.Add(promptTokens, new TagList { { "tenant_id", tenantId.ToString() }, { "token_type", "prompt" } });
            }

            if (completionTokens > 0)
            {
                GatewayDiagnostics.TokensUsed.Add(completionTokens, new TagList { { "tenant_id", tenantId.ToString() }, { "token_type", "completion" } });
            }

            await usageRecorder.RecordAsync(
                tenantId, authenticated.ApiKeyId, providerName, model, streamRequested,
                statusCode, promptTokens, completionTokens, latencyMs, cancellationToken);

            await rateLimitGate.RecordUsageAsync(
                tenantId, authenticated.ApiKeyId, rateLimitCheck, promptTokens + completionTokens, cancellationToken);

            await quotaAlertGate.CheckAndAlertAsync(tenantId, rateLimitCheck.TenantQuota, cancellationToken);

            return result;
        }

        if (!rateLimitCheck.IsAllowed)
        {
            var blockedResult = Results.Json(
                new { error = new { message = $"Token rate limit exceeded for this {rateLimitCheck.BlockedScope}." } },
                statusCode: StatusCodes.Status429TooManyRequests);
            return await FinishAsync(blockedResult, StatusCodes.Status429TooManyRequests, 0, 0);
        }

        var secretStore = services.GetRequiredService<ISecretStore>();
        var providerApiKey = await secretStore.GetSecretAsync(
            ProviderCredentialSecretName.For(tenantId, providerName), cancellationToken);

        if (providerApiKey is null)
        {
            var noCredentialResult = Results.Json(
                new { error = new { message = $"No '{providerName}' credential configured for this tenant." } },
                statusCode: StatusCodes.Status400BadRequest);
            return await FinishAsync(noCredentialResult, StatusCodes.Status400BadRequest, 0, 0);
        }

        var providerClient = services.GetRequiredService<IProviderClientRegistry>().Get(providerName);

        if (streamRequested)
        {
            var httpResponse = httpRequest.HttpContext.Response;

            return Results.Stream(
                async responseBody =>
                {
                    var writer = new HttpResponseStreamWriter(httpResponse);
                    var usage = await providerClient.StreamChatCompletionAsync(
                        requestBody, providerApiKey, writer, cancellationToken);

                    await FinishAsync(
                        Results.Empty, httpResponse.StatusCode, usage?.PromptTokens ?? 0, usage?.CompletionTokens ?? 0);
                },
                contentType: "text/event-stream");
        }

        var providerResponse = await providerClient.CreateChatCompletionAsync(requestBody, providerApiKey, cancellationToken);
        var (promptTokens, completionTokens) = ExtractTokenUsage(providerResponse.Body);

        return await FinishAsync(
            Results.Json(providerResponse.Body, statusCode: providerResponse.StatusCode),
            providerResponse.StatusCode, promptTokens, completionTokens);
    }

    private static (int PromptTokens, int CompletionTokens) ExtractTokenUsage(JsonObject? body)
    {
        if (body?["usage"] is not JsonObject usage)
        {
            return (0, 0);
        }

        var promptTokens = usage["prompt_tokens"]?.GetValue<int>() ?? 0;
        var completionTokens = usage["completion_tokens"]?.GetValue<int>() ?? 0;
        return (promptTokens, completionTokens);
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
