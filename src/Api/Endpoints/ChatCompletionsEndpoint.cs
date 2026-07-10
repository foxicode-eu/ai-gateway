using System.Text.Json.Nodes;
using Core.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Endpoints;

public static class ChatCompletionsEndpoint
{
    public static IEndpointRouteBuilder MapChatCompletions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", HandleAsync);
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

        if (requestBody["model"] is not JsonValue)
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

        // Resolved lazily (rather than as a bound parameter) so provider misconfiguration only surfaces once
        // validation has passed and we actually need to call out — not on every request regardless of shape.
        var providerClient = httpRequest.HttpContext.RequestServices.GetRequiredService<IProviderClient>();
        var providerResponse = await providerClient.CreateChatCompletionAsync(requestBody, cancellationToken);

        return Results.Json(providerResponse.Body, statusCode: providerResponse.StatusCode);
    }
}
