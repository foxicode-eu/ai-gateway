using Core.Auth;
using Microsoft.Net.Http.Headers;

namespace Management.Authentication;

/// <summary>
/// Requires a valid JWT issued by the configured managed IdP on every control-plane request. Unlike the
/// data-plane's <c>TenantAuthenticationFilter</c>, this does not scope the request to a single tenant — any
/// authenticated admin can operate on any tenant, matching Management's existing fully-<see cref="Core.Tenancy.TenantScope.Unscoped"/>
/// trust model (see <c>Program.cs</c>). Per-tenant admin restriction (an admin only managing their own tenant)
/// is a real gap, not yet built — tracked as an open item in ARCHITECTURE.md.
/// </summary>
public sealed class AdminAuthenticationFilter(IJwtAccessTokenValidator jwtValidator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var authorizationHeader = httpContext.Request.Headers[HeaderNames.Authorization].ToString();

        if (!authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized("Missing or malformed Authorization header.");
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            return Unauthorized("Missing or malformed Authorization header.");
        }

        var principal = await jwtValidator.ValidateAsync(token, httpContext.RequestAborted);
        if (principal is null)
        {
            return Unauthorized("Invalid or expired token.");
        }

        return await next(context);
    }

    private static IResult Unauthorized(string message) =>
        Results.Json(new { error = new { message } }, statusCode: StatusCodes.Status401Unauthorized);
}
