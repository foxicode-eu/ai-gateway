namespace Core.Tenancy;

/// <summary>
/// The tenant-scoping mode a <see cref="GatewayDbContext"/> query filter should apply for the current
/// operation. Defaults to <see cref="Blocked"/> so forgetting to set a scope hides data instead of leaking it
/// across tenants.
/// </summary>
public enum TenantScopeMode
{
    /// <summary>No tenant has been set — tenant-scoped queries return no rows.</summary>
    Blocked,

    /// <summary>Queries are filtered to a single tenant.</summary>
    SingleTenant,

    /// <summary>No tenant filter is applied. Reserved for trusted admin/control-plane code paths.</summary>
    Unscoped,
}

public readonly record struct TenantScope
{
    public TenantScopeMode Mode { get; }

    public Guid? TenantId { get; }

    private TenantScope(TenantScopeMode mode, Guid? tenantId)
    {
        Mode = mode;
        TenantId = tenantId;
    }

    public static TenantScope Blocked { get; } = new(TenantScopeMode.Blocked, null);

    public static TenantScope Unscoped { get; } = new(TenantScopeMode.Unscoped, null);

    public static TenantScope ForTenant(Guid tenantId) => new(TenantScopeMode.SingleTenant, tenantId);
}
