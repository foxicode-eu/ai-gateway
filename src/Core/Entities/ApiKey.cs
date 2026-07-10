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
}
