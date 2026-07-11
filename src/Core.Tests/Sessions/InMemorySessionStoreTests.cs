using Core.Sessions;
using Core.Tests.RateLimiting;
using Xunit;

namespace Core.Tests.Sessions;

public class InMemorySessionStoreTests
{
    [Fact]
    public async Task Set_then_get_round_trips_the_value()
    {
        var store = new InMemorySessionStore(TimeProvider.System);

        await store.SetAsync("session-1", "tenant-admin", TimeSpan.FromMinutes(5), CancellationToken.None);
        var value = await store.GetAsync("session-1", CancellationToken.None);

        Assert.Equal("tenant-admin", value);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_session()
    {
        var store = new InMemorySessionStore(TimeProvider.System);

        var value = await store.GetAsync("does-not-exist", CancellationToken.None);

        Assert.Null(value);
    }

    [Fact]
    public async Task Remove_invalidates_the_session()
    {
        var store = new InMemorySessionStore(TimeProvider.System);
        await store.SetAsync("session-1", "value", TimeSpan.FromMinutes(5), CancellationToken.None);

        await store.RemoveAsync("session-1", CancellationToken.None);

        Assert.Null(await store.GetAsync("session-1", CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_session_is_treated_as_absent()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemorySessionStore(clock);
        await store.SetAsync("session-1", "value", TimeSpan.FromMinutes(5), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Null(await store.GetAsync("session-1", CancellationToken.None));
    }
}
