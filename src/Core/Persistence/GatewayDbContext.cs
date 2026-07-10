using Core.Entities;
using Core.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Core.Persistence;

public class GatewayDbContext(DbContextOptions<GatewayDbContext> options, ICurrentTenantAccessor tenantAccessor)
    : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

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
    }
}
