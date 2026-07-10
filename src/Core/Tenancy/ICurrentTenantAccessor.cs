namespace Core.Tenancy;

/// <summary>
/// Exposes the <see cref="TenantScope"/> the current operation should run under. Implementations are expected
/// to be scoped per request/operation (e.g. DI-scoped, or backed by <see cref="AsyncLocal{T}"/>).
/// </summary>
public interface ICurrentTenantAccessor
{
    TenantScope Scope { get; }

    void SetScope(TenantScope scope);
}
