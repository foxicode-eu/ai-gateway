namespace Core.Entities;

public class ApiKey
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    public required string Name { get; set; }

    /// <summary>Hash of the secret key value. The plaintext key is only ever shown once, at creation.</summary>
    public required string KeyHash { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public bool IsActive => RevokedAtUtc is null;

    /// <summary>
    /// Optional finer-grained quota for just this key, on top of (not instead of) the tenant's own
    /// <see cref="Tenant.TokenQuotaPerWindow"/> — both are checked independently. Null means no per-key limit.
    /// </summary>
    public int? TokenQuotaPerWindow { get; set; }
}
