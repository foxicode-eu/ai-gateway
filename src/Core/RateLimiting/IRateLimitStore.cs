namespace Core.RateLimiting;

/// <summary>
/// Minimal counter primitive <see cref="TokenRateLimiter"/> is built on. Deliberately narrow (not a general
/// Redis abstraction) so it's easy to back with either real Redis (production, correct across multiple `Api`
/// instances) or an in-process store (local dev/tests — see <see cref="InMemoryRateLimitStore"/>).
/// </summary>
public interface IRateLimitStore
{
    /// <summary>
    /// Atomically adds <paramref name="amount"/> to the counter at <paramref name="key"/> (creating it at 0
    /// first if absent) and returns the new total. Sets <paramref name="expiry"/> on the key only if it didn't
    /// already have one — repeated increments don't keep pushing the expiry back.
    /// </summary>
    Task<long> IncrementAsync(string key, long amount, TimeSpan expiry, CancellationToken cancellationToken);

    /// <returns>The counter's current value, or 0 if the key doesn't exist (or has expired).</returns>
    Task<long> GetAsync(string key, CancellationToken cancellationToken);
}
