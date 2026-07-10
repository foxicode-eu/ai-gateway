using Core.Entities;
using Core.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Core.Persistence;

public class GatewayDbContext(DbContextOptions<GatewayDbContext> options, ICurrentTenantAccessor tenantAccessor)
    : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("api_keys");
            entity.HasKey(k => k.Id);
            entity.Property(k => k.Name).IsRequired().HasMaxLength(200);
            entity.Property(k => k.KeyHash).IsRequired();
            entity.HasIndex(k => k.KeyHash).IsUnique();

            entity.HasOne(k => k.Tenant)
                .WithMany(t => t.ApiKeys)
                .HasForeignKey(k => k.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            // Fail closed: a scope must be explicitly set to Unscoped or a specific tenant, otherwise no
            // tenant-owned rows are visible. See Core.Tenancy.TenantScope.
            entity.HasQueryFilter(k =>
                tenantAccessor.Scope.Mode == TenantScopeMode.Unscoped ||
                (tenantAccessor.Scope.Mode == TenantScopeMode.SingleTenant &&
                    k.TenantId == tenantAccessor.Scope.TenantId));
        });

        modelBuilder.Entity<UsageEvent>(entity =>
        {
            entity.ToTable("usage_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Model).IsRequired().HasMaxLength(200);
            entity.HasIndex(e => new { e.TenantId, e.CreatedAtUtc });

            entity.HasQueryFilter(e =>
                tenantAccessor.Scope.Mode == TenantScopeMode.Unscoped ||
                (tenantAccessor.Scope.Mode == TenantScopeMode.SingleTenant &&
                    e.TenantId == tenantAccessor.Scope.TenantId));
        });
    }
}
