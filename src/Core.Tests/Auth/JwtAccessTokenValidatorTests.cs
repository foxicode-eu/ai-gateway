using System.Security.Cryptography;
using Core.Auth;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Tests.Auth;

public class JwtAccessTokenValidatorTests
{
    private static AuthenticationOptions CreateOptions(string? signingKey = null) => new()
    {
        Mode = "StaticKey",
        Audience = "ai-gateway-tests",
        TenantIdClaimType = "tenant_id",
        StaticKey = new StaticKeyOptions
        {
            Issuer = "ai-gateway-local-dev",
            SigningKey = signingKey ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        },
    };

    private static JwtAccessTokenValidator CreateValidator(AuthenticationOptions options) =>
        new(Options.Create(options));

    [Fact]
    public async Task Accepts_a_validly_signed_token_and_exposes_the_tenant_id_claim()
    {
        var options = CreateOptions();
        var tenantId = Guid.NewGuid();
        var token = LocalDevTokenIssuer.IssueToken(options, tenantId);

        var principal = await CreateValidator(options).ValidateAsync(token, CancellationToken.None);

        Assert.NotNull(principal);
        Assert.Equal(tenantId.ToString(), principal!.FindFirst("tenant_id")?.Value);
    }

    [Fact]
    public async Task Rejects_an_expired_token()
    {
        var options = CreateOptions();
        var token = LocalDevTokenIssuer.IssueToken(
            options, Guid.NewGuid(), lifetime: TimeSpan.FromMinutes(1), issuedAt: DateTime.UtcNow.AddHours(-2));

        var principal = await CreateValidator(options).ValidateAsync(token, CancellationToken.None);

        Assert.Null(principal);
    }

    [Fact]
    public async Task Rejects_a_token_signed_with_a_different_key()
    {
        var issuingOptions = CreateOptions();
        var validatingOptions = CreateOptions(); // different random key
        var token = LocalDevTokenIssuer.IssueToken(issuingOptions, Guid.NewGuid());

        var principal = await CreateValidator(validatingOptions).ValidateAsync(token, CancellationToken.None);

        Assert.Null(principal);
    }

    [Fact]
    public async Task Rejects_a_token_for_the_wrong_audience()
    {
        var issuingOptions = CreateOptions();
        var validatingOptions = CreateOptions(issuingOptions.StaticKey.SigningKey);
        validatingOptions.Audience = "some-other-audience";
        var token = LocalDevTokenIssuer.IssueToken(issuingOptions, Guid.NewGuid());

        var principal = await CreateValidator(validatingOptions).ValidateAsync(token, CancellationToken.None);

        Assert.Null(principal);
    }

    [Fact]
    public async Task Rejects_a_token_from_the_wrong_issuer()
    {
        var issuingOptions = CreateOptions();
        var validatingOptions = CreateOptions(issuingOptions.StaticKey.SigningKey);
        validatingOptions.StaticKey.Issuer = "someone-else";
        var token = LocalDevTokenIssuer.IssueToken(issuingOptions, Guid.NewGuid());

        var principal = await CreateValidator(validatingOptions).ValidateAsync(token, CancellationToken.None);

        Assert.Null(principal);
    }

    [Fact]
    public async Task Rejects_a_structurally_malformed_token()
    {
        var options = CreateOptions();

        var principal = await CreateValidator(options).ValidateAsync("not-a-jwt", CancellationToken.None);

        Assert.Null(principal);
    }

    [Fact]
    public void LocalDevTokenIssuer_refuses_to_run_outside_StaticKey_mode()
    {
        var options = CreateOptions();
        options.Mode = "OidcAuthority";

        Assert.Throws<InvalidOperationException>(() => LocalDevTokenIssuer.IssueToken(options, Guid.NewGuid()));
    }
}
