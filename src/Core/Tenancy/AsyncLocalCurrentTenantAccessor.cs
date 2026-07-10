namespace Core.Tenancy;

/// <summary>
/// Default <see cref="ICurrentTenantAccessor"/> backed by <see cref="AsyncLocal{T}"/>, so scope flows with the
/// current async call chain (e.g. a single HTTP request) without needing DI-scoped lifetime wiring.
/// Starts <see cref="TenantScope.Blocked"/> until something explicitly sets a scope.
/// </summary>
public sealed class AsyncLocalCurrentTenantAccessor : ICurrentTenantAccessor
{
    private static readonly AsyncLocal<TenantScope> AmbientScope = new();

    public TenantScope Scope => AmbientScope.Value;

    public void SetScope(TenantScope scope) => AmbientScope.Value = scope;
}
