using StackExchange.Redis;

namespace Core.RateLimiting;

/// <summary>Production <see cref="IRateLimitStore"/> — correct across multiple `Api` instances.</summary>
public sealed class RedisRateLimitStore(IConnectionMultiplexer redis) : IRateLimitStore
{
    public async Task<long> IncrementAsync(string key, long amount, TimeSpan expiry, CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        var newValue = await db.StringIncrementAsync(key, amount);
        // Only set an expiry if the key doesn't have one yet, so repeated increments within the same window
        // don't keep pushing it back — a fresh window should expire `expiry` after it was first touched.
        await db.KeyExpireAsync(key, expiry, ExpireWhen.HasNoExpiry);
        return newValue;
    }

    public async Task<long> GetAsync(string key, CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        var value = await db.StringGetAsync(key);
        return value.IsNullOrEmpty ? 0 : (long)value;
    }
}
