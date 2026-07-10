using System.Text.Json.Nodes;
using Api.Authentication;
using Core.Providers;
using Core.Secrets;
using Core.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Endpoints;

public static class ChatCompletionsEndpoint
{
    public static IEndpointRouteBuilder MapChatCompletions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", HandleAsync)
            .AddEndpointFilter<ApiKeyAuthenticationFilter>();
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

        // Streaming isn't implemented yet (planned for a later phase) — reject explicitly rather than silently
        // falling back to a non-streaming response the client didn't ask for.
        var streamRequested = requestBody.TryGetPropertyValue("stream", out var streamNode)
            && streamNode is JsonValue streamValue
            && streamValue.TryGetValue<bool>(out var isStream)
            && isStream;

        if (streamRequested)
        {
            return Results.Json(
                new { error = new { message = "stream:true is not supported yet." } },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // The auth filter set the tenant scope before this handler ran; it's guaranteed to be a single tenant.
        var services = httpRequest.HttpContext.RequestServices;
        var tenantId = services.GetRequiredService<ICurrentTenantAccessor>().Scope.TenantId!.Value;

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
        var providerResponse = await providerClient.CreateChatCompletionAsync(requestBody, providerApiKey, cancellationToken);

        return Results.Json(providerResponse.Body, statusCode: providerResponse.StatusCode);
    }
}
