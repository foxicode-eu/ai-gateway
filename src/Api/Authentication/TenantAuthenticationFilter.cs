using Core.Auth;
using Core.Persistence;
using Core.Security;
using Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Api.Authentication;

/// <summary>
/// Resolves the tenant for a data-plane request from its <c>Authorization</c> header and sets the ambient
/// <see cref="TenantScope"/> for the rest of the request. Accepts two credential shapes:
/// <list type="bullet">
/// <item>A JWT access token issued by the configured managed IdP (see <see cref="Core.Auth.AuthenticationOptions"/>)
/// — the intended long-term mechanism per ARCHITECTURE.md, carrying a <c>tenant_id</c> claim.</item>
/// <item>A raw tenant API key (<see cref="IApiKeyAuthenticator"/>, from Phase 3) — kept working because true
/// per-tenant OAuth2 client-credentials issuance requires dynamic client registration with a real IdP, which
/// is not wired up yet (no live IdP account to build/verify against). Remove this branch once that lands.</item>
/// </list>
/// Apply via <c>.AddEndpointFilter&lt;TenantAuthenticationFilter&gt;()</c> on routes that require it — this is
/// not global middleware, so unauthenticated routes (like the discovery endpoint) are unaffected.
/// </summary>
public sealed class TenantAuthenticationFilter(
    IApiKeyAuthenticator apiKeyAuthenticator,
    IJwtAccessTokenValidator jwtValidator,
    IOptions<AuthenticationOptions> authenticationOptions,
    GatewayDbContext dbContext,
    ICurrentTenantAccessor tenantAccessor)
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

        var credential = authorizationHeader["Bearer ".Length..].Trim();
        if (credential.Length == 0)
        {
            return Unauthorized("Missing or malformed Authorization header.");
        }

        var authenticated = LooksLikeJwt(credential)
            ? await AuthenticateJwtAsync(credential, httpContext.RequestAborted)
            : await AuthenticateApiKeyAsync(credential, httpContext.RequestAborted);

        if (authenticated is null)
        {
            return Unauthorized("Invalid, expired, or revoked credential.");
        }

        tenantAccessor.SetScope(TenantScope.ForTenant(authenticated.TenantId));
        httpContext.Items[nameof(AuthenticatedTenant)] = authenticated;

        return await next(context);
    }

    // JWTs are three base64url segments joined by '.'; raw API keys (Core.Security.ApiKeyGenerator, "sk-gw-...")
    // never contain a '.'. Cheap and sufficient to route between the two credential shapes.
    private static bool LooksLikeJwt(string credential) => credential.Count(c => c == '.') == 2;

    private async Task<AuthenticatedTenant?> AuthenticateJwtAsync(string token, CancellationToken cancellationToken)
    {
        var principal = await jwtValidator.ValidateAsync(token, cancellationToken);
        var tenantIdClaim = principal?.FindFirst(authenticationOptions.Value.TenantIdClaimType)?.Value;

        if (tenantIdClaim is null || !Guid.TryParse(tenantIdClaim, out var tenantId))
        {
            return null;
        }

        // Defensive: the IdP isn't yet the source of truth for which tenants exist (no dynamic client
        // registration wired up), so a syntactically valid tenant_id claim isn't automatically trustworthy.
        var tenantExists = await dbContext.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken);
        return tenantExists ? new AuthenticatedTenant(tenantId, ApiKeyId: null) : null;
    }

    private async Task<AuthenticatedTenant?> AuthenticateApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        var result = await apiKeyAuthenticator.AuthenticateAsync(apiKey, cancellationToken);
        return result is null ? null : new AuthenticatedTenant(result.TenantId, result.ApiKeyId);
    }

    private static IResult Unauthorized(string message) =>
        Results.Json(new { error = new { message } }, statusCode: StatusCodes.Status401Unauthorized);
}
