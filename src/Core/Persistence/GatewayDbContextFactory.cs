using Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Core.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> tooling construct a <see cref="GatewayDbContext"/> without a full host/DI container.
/// Only used at migration-authoring time — the connection string here never talks to a real database.
/// </summary>
public class GatewayDbContextFactory : IDesignTimeDbContextFactory<GatewayDbContext>
{
    public GatewayDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("GATEWAY_DB_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=ai_gateway;Username=ai_gateway;Password=ai_gateway";

        var optionsBuilder = new DbContextOptionsBuilder<GatewayDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new GatewayDbContext(optionsBuilder.Options, new AsyncLocalCurrentTenantAccessor());
    }
}
