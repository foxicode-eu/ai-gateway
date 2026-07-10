using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Core.Auth;

/// <summary>
/// Validates JWT access tokens issued by whichever managed IdP is configured. In "OidcAuthority" mode this is
/// the same signature/expiry/issuer/audience validation ASP.NET Core's own JWT bearer handler performs — signing
/// keys are fetched from the IdP's OpenID Connect discovery document and cached/auto-refreshed by
/// <see cref="ConfigurationManager{T}"/>, not hardcoded. "StaticKey" mode skips the network round trip entirely
/// and is for local development and automated tests only (see <see cref="LocalDevTokenIssuer"/>).
/// </summary>
public sealed class JwtAccessTokenValidator : IJwtAccessTokenValidator
{
    private readonly AuthenticationOptions _options;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();
    private readonly Lazy<ConfigurationManager<OpenIdConnectConfiguration>> _oidcConfigurationManager;

    public JwtAccessTokenValidator(IOptions<AuthenticationOptions> options)
    {
        _options = options.Value;
        _oidcConfigurationManager = new Lazy<ConfigurationManager<OpenIdConnectConfiguration>>(CreateOidcConfigurationManager);
    }

    public async Task<ClaimsPrincipal?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var validationParameters = await BuildValidationParametersAsync(cancellationToken);
            var principal = _tokenHandler.ValidateToken(token, validationParameters, out _);
            return principal;
        }
        catch (SecurityTokenException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // Thrown for structurally malformed tokens (e.g. not a JWT at all).
            return null;
        }
    }

    private async Task<TokenValidationParameters> BuildValidationParametersAsync(CancellationToken cancellationToken)
    {
        if (_options.Mode == "StaticKey")
        {
            return new TokenValidationParameters
            {
                ValidIssuer = _options.StaticKey.Issuer,
                ValidAudience = _options.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(_options.StaticKey.SigningKey)),
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        }

        if (_options.Mode == "OidcAuthority")
        {
            var oidcConfiguration = await _oidcConfigurationManager.Value.GetConfigurationAsync(cancellationToken);
            return new TokenValidationParameters
            {
                ValidIssuer = oidcConfiguration.Issuer,
                ValidAudience = _options.Audience,
                IssuerSigningKeys = oidcConfiguration.SigningKeys,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        }

        throw new InvalidOperationException(
            $"Unrecognized '{AuthenticationOptions.ConfigurationSection}:Mode' value \"{_options.Mode}\" (expected \"OidcAuthority\" or \"StaticKey\").");
    }

    private ConfigurationManager<OpenIdConnectConfiguration> CreateOidcConfigurationManager()
    {
        if (string.IsNullOrWhiteSpace(_options.Authority))
        {
            throw new InvalidOperationException(
                $"'{AuthenticationOptions.ConfigurationSection}:Authority' is required when Mode is \"OidcAuthority\".");
        }

        var metadataAddress = $"{_options.Authority.TrimEnd('/')}/.well-known/openid-configuration";
        return new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress, new OpenIdConnectConfigurationRetriever());
    }
}
