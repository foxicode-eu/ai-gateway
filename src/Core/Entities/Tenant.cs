namespace Core.Entities;

public class Tenant
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public List<ApiKey> ApiKeys { get; set; } = [];
}
