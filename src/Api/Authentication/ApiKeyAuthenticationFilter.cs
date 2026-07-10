using Core.Security;
using Core.Tenancy;
using Microsoft.Net.Http.Headers;

namespace Api.Authentication;

/// <summary>
/// Resolves the tenant for a data-plane request from its <c>Authorization: Bearer &lt;key&gt;</c> header and
/// sets the ambient <see cref="TenantScope"/> for the rest of the request. Apply via
/// <c>.AddEndpointFilter&lt;ApiKeyAuthenticationFilter&gt;()</c> on routes that require it — this is not global
/// middleware, so unauthenticated routes (like the discovery endpoint) are unaffected.
/// </summary>
public sealed class ApiKeyAuthenticationFilter(IApiKeyAuthenticator authenticator, ICurrentTenantAccessor tenantAccessor)
    : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var authorizationHeader = httpContext.Request.Headers[HeaderNames.Authorization].ToString();

        if (!authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized("Missing or malformed Authorization header.");
        }

        var apiKey = authorizationHeader["Bearer ".Length..].Trim();
        if (apiKey.Length == 0)
        {
            return Unauthorized("Missing or malformed Authorization header.");
        }

        var result = await authenticator.AuthenticateAsync(apiKey, httpContext.RequestAborted);
        if (result is null)
        {
            return Unauthorized("Invalid or revoked API key.");
        }

        tenantAccessor.SetScope(TenantScope.ForTenant(result.TenantId));
        httpContext.Items[nameof(ApiKeyAuthenticationResult)] = result;

        return await next(context);
    }

    private static IResult Unauthorized(string message) =>
        Results.Json(new { error = new { message } }, statusCode: StatusCodes.Status401Unauthorized);
}
