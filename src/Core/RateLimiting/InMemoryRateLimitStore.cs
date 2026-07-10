using System.Collections.Concurrent;

namespace Core.RateLimiting;

/// <summary>
/// Local-development/test-only <see cref="IRateLimitStore"/>. Correct within a single process, but doesn't
/// coordinate across multiple `Api` instances — production must use <see cref="RedisRateLimitStore"/> (that's
/// the whole reason ARCHITECTURE.md calls for Redis here: counters need to be shared across replicas).
/// </summary>
public sealed class InMemoryRateLimitStore(TimeProvider timeProvider) : IRateLimitStore
{
    private sealed record Entry(long Count, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public Task<long> IncrementAsync(string key, long amount, TimeSpan expiry, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        long result = 0;

        _entries.AddOrUpdate(
            key,
            _ =>
            {
                result = amount;
                return new Entry(amount, now + expiry);
            },
            (_, existing) =>
            {
                var stillLive = existing.ExpiresAt > now;
                var newCount = (stillLive ? existing.Count : 0) + amount;
                var expiresAt = stillLive ? existing.ExpiresAt : now + expiry;
                result = newCount;
                return new Entry(newCount, expiresAt);
            });

        return Task.FromResult(result);
    }

    public Task<long> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > timeProvider.GetUtcNow())
        {
            return Task.FromResult(entry.Count);
        }

        return Task.FromResult(0L);
    }
}
