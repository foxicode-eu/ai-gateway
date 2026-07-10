namespace Core.Auth;

/// <summary>
/// Configuration for validating JWTs issued by a managed identity provider (Entra ID, Auth0, ...). The gateway
/// only ever validates tokens here — it never issues them itself (see ARCHITECTURE.md's AuthN/AuthZ section).
/// </summary>
public sealed class AuthenticationOptions
{
    public const string ConfigurationSection = "Authentication";

    /// <summary>"OidcAuthority" (production — validate against a real IdP's discovery document) or
    /// "StaticKey" (local development and tests only — see <see cref="LocalDevTokenIssuer"/>).</summary>
    public string Mode { get; set; } = "OidcAuthority";

    /// <summary>Required in "OidcAuthority" mode. The IdP's issuer URL, e.g. an Auth0 tenant or Entra ID authority.</summary>
    public string? Authority { get; set; }

    /// <summary>Required in both modes — the audience value tokens must be issued for.</summary>
    public required string Audience { get; set; }

    /// <summary>Claim type carrying the gateway tenant ID (a GUID) in the validated token.</summary>
    public string TenantIdClaimType { get; set; } = "tenant_id";

    public StaticKeyOptions StaticKey { get; set; } = new();
}

/// <summary>Local-development/test-only signing configuration — never point real tenant traffic at this.</summary>
public sealed class StaticKeyOptions
{
    /// <summary>Base64-encoded symmetric signing key, at least 32 bytes (HS256 minimum).</summary>
    public string SigningKey { get; set; } = "";

    public string Issuer { get; set; } = "ai-gateway-local-dev";
}
