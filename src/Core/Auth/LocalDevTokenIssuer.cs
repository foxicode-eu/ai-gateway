using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Core.Auth;

/// <summary>
/// Mints JWTs signed with the "StaticKey" configuration, for local development and automated tests only. This
/// is deliberately not wired to any HTTP endpoint — the gateway never issues tokens over the wire (see
/// ARCHITECTURE.md). Used directly by tests and by the <c>DevTools</c> CLI for manual local verification.
/// </summary>
public static class LocalDevTokenIssuer
{
    public static string IssueToken(
        AuthenticationOptions options, Guid tenantId, TimeSpan? lifetime = null, DateTime? issuedAt = null)
    {
        if (options.Mode != "StaticKey")
        {
            throw new InvalidOperationException(
                $"{nameof(LocalDevTokenIssuer)} only works with Mode \"StaticKey\" (got \"{options.Mode}\") — it must never be used to mint tokens for a real IdP.");
        }

        var signingKey = new SymmetricSecurityKey(Convert.FromBase64String(options.StaticKey.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var claims = new[] { new Claim(options.TenantIdClaimType, tenantId.ToString()) };
        var notBefore = issuedAt ?? DateTime.UtcNow;

        var token = new JwtSecurityToken(
            issuer: options.StaticKey.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: notBefore,
            expires: notBefore.Add(lifetime ?? TimeSpan.FromHours(1)),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
