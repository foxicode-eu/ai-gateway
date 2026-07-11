using StackExchange.Redis;

namespace Core.Sessions;

/// <summary>Production <see cref="ISessionStore"/>.</summary>
public sealed class RedisSessionStore(IConnectionMultiplexer redis) : ISessionStore
{
    public async Task SetAsync(string sessionId, string value, TimeSpan expiry, CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        await db.StringSetAsync(Key(sessionId), value, expiry);
    }

    public async Task<string?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        var value = await db.StringGetAsync(Key(sessionId));
        return value.IsNullOrEmpty ? null : value.ToString();
    }

    public async Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        await db.KeyDeleteAsync(Key(sessionId));
    }

    private static string Key(string sessionId) => $"session:{sessionId}";
}
