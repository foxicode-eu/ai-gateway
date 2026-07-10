using Core.RateLimiting;
using Xunit;

namespace Core.Tests.RateLimiting;

public class TokenRateLimiterTests
{
    // A fixed, deterministic instant that lands exactly on a 60-second window boundary, so tests can reason
    // about elapsed fraction precisely by advancing a known number of seconds from here.
    private static readonly DateTimeOffset WindowStart = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 - (1_700_000_000 % 60));

    private static (TokenRateLimiter Limiter, ManualTimeProvider Clock) CreateLimiter()
    {
        var clock = new ManualTimeProvider(WindowStart);
        var store = new InMemoryRateLimitStore(clock);
        return (new TokenRateLimiter(store, clock), clock);
    }

    [Fact]
    public async Task Allows_requests_when_under_the_limit()
    {
        var (limiter, _) = CreateLimiter();

        var status = await limiter.CheckAsync("tenant:a", limit: 1000, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(status.IsAllowed);
        Assert.Equal(0, status.EstimatedUsage);
    }

    [Fact]
    public async Task Blocks_requests_once_usage_in_the_current_window_reaches_the_limit()
    {
        var (limiter, _) = CreateLimiter();
        var window = TimeSpan.FromSeconds(60);

        await limiter.RecordUsageAsync("tenant:a", 1000, window, CancellationToken.None);
        var status = await limiter.CheckAsync("tenant:a", limit: 1000, window, CancellationToken.None);

        Assert.False(status.IsAllowed);
    }

    [Fact]
    public async Task Different_keys_are_independent()
    {
        var (limiter, _) = CreateLimiter();
        var window = TimeSpan.FromSeconds(60);

        await limiter.RecordUsageAsync("tenant:a", 1000, window, CancellationToken.None);
        var statusA = await limiter.CheckAsync("tenant:a", limit: 1000, window, CancellationToken.None);
        var statusB = await limiter.CheckAsync("tenant:b", limit: 1000, window, CancellationToken.None);

        Assert.False(statusA.IsAllowed);
        Assert.True(statusB.IsAllowed);
    }

    [Fact]
    public async Task A_full_previous_window_of_usage_fully_counts_at_the_start_of_the_next_window()
    {
        var (limiter, clock) = CreateLimiter();
        var window = TimeSpan.FromSeconds(60);

        await limiter.RecordUsageAsync("tenant:a", 1000, window, CancellationToken.None);
        clock.Advance(window); // exactly one window later — elapsed fraction of the new window is 0

        var status = await limiter.CheckAsync("tenant:a", limit: 1000, window, CancellationToken.None);

        Assert.Equal(1000, status.EstimatedUsage);
        Assert.False(status.IsAllowed);
    }

    [Fact]
    public async Task Previous_window_usage_decays_linearly_as_the_current_window_elapses()
    {
        var (limiter, clock) = CreateLimiter();
        var window = TimeSpan.FromSeconds(60);

        await limiter.RecordUsageAsync("tenant:a", 1000, window, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(90)); // 1 full window + half of the next

        var status = await limiter.CheckAsync("tenant:a", limit: 10_000, window, CancellationToken.None);

        Assert.Equal(500, status.EstimatedUsage, precision: 3);
    }

    [Fact]
    public async Task Usage_older_than_two_windows_no_longer_counts_at_all()
    {
        var (limiter, clock) = CreateLimiter();
        var window = TimeSpan.FromSeconds(60);

        await limiter.RecordUsageAsync("tenant:a", 1000, window, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(150)); // 2.5 windows later

        var status = await limiter.CheckAsync("tenant:a", limit: 10, window, CancellationToken.None);

        Assert.Equal(0, status.EstimatedUsage);
        Assert.True(status.IsAllowed);
    }

    [Fact]
    public async Task Usage_accumulates_across_multiple_recordings_in_the_same_window()
    {
        var (limiter, _) = CreateLimiter();
        var window = TimeSpan.FromSeconds(60);

        await limiter.RecordUsageAsync("tenant:a", 400, window, CancellationToken.None);
        await limiter.RecordUsageAsync("tenant:a", 400, window, CancellationToken.None);
        var status = await limiter.CheckAsync("tenant:a", limit: 1000, window, CancellationToken.None);

        Assert.Equal(800, status.EstimatedUsage);
        Assert.True(status.IsAllowed);
    }
}
