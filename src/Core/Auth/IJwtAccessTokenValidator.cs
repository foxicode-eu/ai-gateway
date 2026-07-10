using System.Security.Claims;

namespace Core.Auth;

public interface IJwtAccessTokenValidator
{
    /// <returns>The token's claims if it's a validly signed, non-expired token for the configured audience
    /// (and issuer, in "OidcAuthority" mode) — null otherwise. Never throws for malformed/invalid tokens.</returns>
    Task<ClaimsPrincipal?> ValidateAsync(string token, CancellationToken cancellationToken);
}
