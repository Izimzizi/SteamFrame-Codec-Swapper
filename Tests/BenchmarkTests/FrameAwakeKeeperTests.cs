using SteamLinkVRSettings.Frame;

/// <summary>The activity-lease renewal schedule, without a headset.</summary>
public class FrameAwakeKeeperTests
{
    [Fact]
    public async Task Renews_on_schedule_and_ends_the_extension_on_dispose()
    {
        var calls = new List<int>();
        var ticks = new SemaphoreSlim(0);
        var keeper = new FrameAwakeKeeper((seconds, _) => { lock (calls) calls.Add(seconds); return Task.CompletedTask; },
            async (wait, token) => { Assert.Equal(FrameAwakeKeeper.RenewEvery, wait); await ticks.WaitAsync(token); });
        await keeper.StartAsync(CancellationToken.None);
        Assert.True(keeper.Active); Assert.Equal(1, keeper.Renewals);
        ticks.Release(); ticks.Release();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (keeper.Renewals < 3 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(3, keeper.Renewals);
        await keeper.DisposeAsync();
        Assert.False(keeper.Active);
        lock (calls) Assert.Equal([60, 60, 60, 0], calls);
        // Nothing renews after dispose, even if the clock keeps ticking.
        ticks.Release();
        await Task.Delay(50);
        lock (calls) Assert.Equal(4, calls.Count);
        await keeper.DisposeAsync(); // Idempotent.
    }

    [Fact]
    public async Task A_failed_first_extension_propagates_and_later_failures_are_retried()
    {
        var failing = new FrameAwakeKeeper((_, _) => throw new IOException("no headset"));
        await Assert.ThrowsAsync<IOException>(() => failing.StartAsync(CancellationToken.None));
        Assert.False(failing.Active);
        await failing.DisposeAsync();

        var attempts = 0;
        var ticks = new SemaphoreSlim(0);
        var flaky = new FrameAwakeKeeper((seconds, _) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (seconds == 60 && attempt == 2) throw new IOException("transient");
            return Task.CompletedTask;
        }, async (_, token) => await ticks.WaitAsync(token));
        await flaky.StartAsync(CancellationToken.None);
        ticks.Release();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (flaky.LastError is null && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.IsType<IOException>(flaky.LastError);
        Assert.Equal(1, flaky.Renewals);
        ticks.Release();
        while (flaky.Renewals < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(2, flaky.Renewals); Assert.Null(flaky.LastError);
        await flaky.DisposeAsync();
    }
}
