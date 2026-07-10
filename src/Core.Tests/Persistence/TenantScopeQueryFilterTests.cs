using Core.Entities;
using Core.Persistence;
using Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Core.Tests.Persistence;

public class TenantScopeQueryFilterTests
{
    private static GatewayDbContext CreateContext(ICurrentTenantAccessor tenantAccessor)
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new GatewayDbContext(options, tenantAccessor);
    }

    private static async Task SeedTwoTenantsAsync(GatewayDbContext context, Guid tenantA, Guid tenantB)
    {
        context.Tenants.Add(new Tenant { Id = tenantA, Name = "Tenant A", CreatedAtUtc = DateTimeOffset.UtcNow });
        context.Tenants.Add(new Tenant { Id = tenantB, Name = "Tenant B", CreatedAtUtc = DateTimeOffset.UtcNow });
        context.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(), TenantId = tenantA, Name = "a-key", KeyHash = "hash-a",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        context.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(), TenantId = tenantB, Name = "b-key", KeyHash = "hash-b",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Blocked_scope_returns_no_api_keys()
    {
        var accessor = new AsyncLocalCurrentTenantAccessor();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using var context = CreateContext(accessor);
        await SeedTwoTenantsAsync(context, tenantA, tenantB);

        accessor.SetScope(TenantScope.Blocked);

        var keys = await context.ApiKeys.ToListAsync();

        Assert.Empty(keys);
    }

    [Fact]
    public async Task SingleTenant_scope_only_returns_that_tenants_api_keys()
    {
        var accessor = new AsyncLocalCurrentTenantAccessor();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using var context = CreateContext(accessor);
        await SeedTwoTenantsAsync(context, tenantA, tenantB);

        accessor.SetScope(TenantScope.ForTenant(tenantA));

        var keys = await context.ApiKeys.ToListAsync();

        var key = Assert.Single(keys);
        Assert.Equal(tenantA, key.TenantId);
    }

    [Fact]
    public async Task Unscoped_scope_returns_api_keys_for_all_tenants()
    {
        var accessor = new AsyncLocalCurrentTenantAccessor();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using var context = CreateContext(accessor);
        await SeedTwoTenantsAsync(context, tenantA, tenantB);

        accessor.SetScope(TenantScope.Unscoped);

        var keys = await context.ApiKeys.ToListAsync();

        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public async Task Default_scope_before_anything_is_set_is_blocked()
    {
        var accessor = new AsyncLocalCurrentTenantAccessor();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using var context = CreateContext(accessor);
        await SeedTwoTenantsAsync(context, tenantA, tenantB);

        var keys = await context.ApiKeys.ToListAsync();

        Assert.Empty(keys);
    }
}
