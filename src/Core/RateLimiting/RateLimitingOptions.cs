namespace Core.RateLimiting;

public sealed class RateLimitingOptions
{
    public const string ConfigurationSection = "RateLimiting";

    /// <summary>"Redis" (production — correct across multiple `Api` instances) or "InMemory" (local dev/tests only).</summary>
    public string Store { get; set; } = "Redis";

    /// <summary>Required when <see cref="Store"/> is "Redis" — a StackExchange.Redis connection string.</summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>The fixed window size the sliding-window counter is built from. See <see cref="TokenRateLimiter"/>.</summary>
    public int WindowSeconds { get; set; } = 60;
}
