namespace Core.RateLimiting;

/// <summary>
/// Sliding-window-counter rate limiter: a weighted blend of the current and previous fixed windows, keyed on
/// two Redis (or in-memory) counters rather than a full event log. This is an approximation of a true sliding
/// window (assumes usage within each fixed window is spread evenly, which it isn't exactly) but is the standard
/// practical trade-off — O(1) store operations per check instead of storing every individual event.
/// </summary>
public sealed class TokenRateLimiter(IRateLimitStore store, TimeProvider timeProvider) : ITokenRateLimiter
{
    public async Task<RateLimitStatus> CheckAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken)
    {
        var (currentKey, previousKey, elapsedFraction) = WindowKeys(key, window);

        var currentCount = await store.GetAsync(currentKey, cancellationToken);
        var previousCount = await store.GetAsync(previousKey, cancellationToken);

        var estimatedUsage = currentCount + (previousCount * (1 - elapsedFraction));

        return new RateLimitStatus(estimatedUsage < limit, estimatedUsage, limit);
    }

    public async Task RecordUsageAsync(string key, int tokens, TimeSpan window, CancellationToken cancellationToken)
    {
        var (currentKey, _, _) = WindowKeys(key, window);

        // Kept alive for up to two windows so it's still readable as "the previous window" for the entirety of
        // the window that follows it.
        await store.IncrementAsync(currentKey, tokens, window * 2, cancellationToken);
    }

    private (string CurrentKey, string PreviousKey, double ElapsedFraction) WindowKeys(string key, TimeSpan window)
    {
        var windowSeconds = (long)window.TotalSeconds;
        var nowSeconds = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var windowIndex = nowSeconds / windowSeconds;
        var elapsedFraction = (nowSeconds % windowSeconds) / (double)windowSeconds;

        return ($"{key}:{windowIndex}", $"{key}:{windowIndex - 1}", elapsedFraction);
    }
}
