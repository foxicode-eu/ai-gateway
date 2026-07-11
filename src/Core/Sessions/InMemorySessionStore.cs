using System.Collections.Concurrent;

namespace Core.Sessions;

/// <summary>Local-development/test-only <see cref="ISessionStore"/>. Production must use <see cref="RedisSessionStore"/>.</summary>
public sealed class InMemorySessionStore(TimeProvider timeProvider) : ISessionStore
{
    private sealed record Entry(string Value, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public Task SetAsync(string sessionId, string value, TimeSpan expiry, CancellationToken cancellationToken)
    {
        _entries[sessionId] = new Entry(value, timeProvider.GetUtcNow() + expiry);
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(sessionId, out var entry) && entry.ExpiresAt > timeProvider.GetUtcNow())
        {
            return Task.FromResult<string?>(entry.Value);
        }

        return Task.FromResult<string?>(null);
    }

    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        _entries.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }
}
