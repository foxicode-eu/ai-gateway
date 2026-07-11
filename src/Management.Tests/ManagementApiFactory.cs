using System.Net.Http.Headers;
using System.Security.Cryptography;
using Core.Auth;
using Core.Persistence;
using Core.Secrets;
using Core.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Management.Tests;

/// <summary>Shared test host: real EF Core pipeline (InMemory provider) and an in-memory secret store.</summary>
public sealed class ManagementApiFactory : WebApplicationFactory<Program>
{
    public InMemorySecretStore SecretStore { get; } = new();

    // Captured once, outside the configure lambda below — the lambda can run more than once per factory, and
    // generating the name inline would hand each run a fresh, empty database instead of a shared one.
    private readonly string _databaseName = Guid.NewGuid().ToString();

    // Test-owned auth config, deliberately independent of appsettings.Development.json's values — tests
    // shouldn't break if the shared local-dev signing key ever changes.
    public readonly AuthenticationOptions AuthenticationOptions = new()
    {
        Mode = "StaticKey",
        Audience = "management-tests",
        StaticKey = new StaticKeyOptions
        {
            Issuer = "management-tests",
            SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        },
    };

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<GatewayDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<GatewayDbContext>>();
            services.AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase(_databaseName));

            services.RemoveAll<ISecretStore>();
            services.AddSingleton<ISecretStore>(SecretStore);

            services.RemoveAll<ISessionStore>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<ISessionStore, InMemorySessionStore>();

            services.Configure<AuthenticationOptions>(o =>
            {
                o.Mode = AuthenticationOptions.Mode;
                o.Audience = AuthenticationOptions.Audience;
                o.TenantIdClaimType = AuthenticationOptions.TenantIdClaimType;
                o.StaticKey = AuthenticationOptions.StaticKey;
            });
        });
    }

    /// <summary>An <see cref="HttpClient"/> pre-authenticated as a trusted admin (valid JWT, any tenant_id).</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        var token = LocalDevTokenIssuer.IssueToken(AuthenticationOptions, Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task SetSecretAsync(string name, string value, CancellationToken cancellationToken)
        {
            _values[name] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(_values.TryGetValue(name, out var value) ? value : null);

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken)
        {
            _values.Remove(name);
            return Task.CompletedTask;
        }
    }
}
