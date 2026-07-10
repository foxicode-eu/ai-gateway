namespace Core.RateLimiting;

public sealed record RateLimitStatus(bool IsAllowed, double EstimatedUsage, int Limit);

public interface ITokenRateLimiter
{
    /// <summary>
    /// Checks whether <paramref name="key"/> is currently under <paramref name="limit"/> tokens for the given
    /// window — does not consume anything. Call before proxying a request; token counts aren't known until a
    /// completion finishes, so admission control here is necessarily an estimate based on prior usage, not a
    /// hard per-request reservation.
    /// </summary>
    Task<RateLimitStatus> CheckAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>Records actual token usage against <paramref name="key"/> after a completion finishes.</summary>
    Task RecordUsageAsync(string key, int tokens, TimeSpan window, CancellationToken cancellationToken);
}
