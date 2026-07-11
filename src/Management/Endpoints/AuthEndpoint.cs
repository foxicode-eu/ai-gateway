using Core.Auth;
using Management.Authentication;

namespace Management.Endpoints;

/// <summary>
/// The session-issuing surface: exchanges a bearer JWT for an HttpOnly session cookie (<see cref="SessionCookies"/>).
/// Not behind <see cref="AdminAuthenticationFilter"/> — that's what this endpoint gets you *into*. Today the JWT
/// comes from <c>LocalDevTokenIssuer</c>/`DevTools` (dev-only); swapping in a real IdP later only changes how the
/// Dashboard obtains that JWT (an OIDC authorization-code+PKCE redirect instead of a paste-a-token form) — this
/// exchange step and everything downstream of it (session storage, cookie, `AdminAuthenticationFilter`) doesn't
/// need to change.
/// </summary>
public static class AuthEndpoint
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", LoginAsync);
        app.MapPost("/auth/logout", LogoutAsync);
        app.MapGet("/auth/session", GetSessionAsync);
        return app;
    }

    public sealed record LoginRequest(string Token);

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        IJwtAccessTokenValidator jwtValidator,
        SessionCookies sessionCookies,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return Results.BadRequest(new { error = new { message = "token is required." } });
        }

        var principal = await jwtValidator.ValidateAsync(request.Token, cancellationToken);
        if (principal is null)
        {
            return Results.Json(
                new { error = new { message = "Invalid or expired token." } },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst("tenant_id")?.Value ?? "admin";
        await sessionCookies.SignInAsync(httpContext, subject, cancellationToken);

        return Results.Ok(new { authenticated = true });
    }

    private static async Task<IResult> LogoutAsync(
        SessionCookies sessionCookies, HttpContext httpContext, CancellationToken cancellationToken)
    {
        await sessionCookies.SignOutAsync(httpContext, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetSessionAsync(
        SessionCookies sessionCookies, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var isValid = await sessionCookies.IsValidAsync(httpContext, cancellationToken);
        return isValid
            ? Results.Ok(new { authenticated = true })
            : Results.Json(new { authenticated = false }, statusCode: StatusCodes.Status401Unauthorized);
    }
}
