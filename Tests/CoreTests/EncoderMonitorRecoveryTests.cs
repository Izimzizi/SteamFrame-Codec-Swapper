using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Tests;

public class EncoderMonitorRecoveryTests
{
    private static readonly EncoderSnapshot Old = new() { ProcessId = 10, Supported = true, RequiresSteamVrRestart = true };
    private static readonly EncoderSnapshot Ready = new() { ProcessId = 20, Supported = true };
    private static readonly EncoderSnapshot Started = Ready with { Installed = true };

    [Fact]
    public async Task UpgradeWaitsForReplacementDriverAndRestartsOnlyOnce()
    {
        var calls = new List<string>();
        var readings = new Queue<EncoderSnapshot>([Old, new() { WaitingForDriver = true }, Ready with { Supported = false, WaitingForDriver = true }, Ready]);
        var starts = 0;
        var result = await EncoderMonitorRecovery.StartAsync(
            () => { calls.Add("start"); return Task.FromResult(++starts == 1 ? Old : Started); },
            () => { calls.Add("read"); return Task.FromResult(readings.Dequeue()); },
            pid => { Assert.Equal(10, pid); calls.Add("restart"); return Task.CompletedTask; },
            _ => { }, TestContext.Current.CancellationToken, wait: _ => Task.CompletedTask);
        Assert.True(result.Restarted);
        Assert.Equal(Started, result.Snapshot);
        Assert.Equal(new[] { "start", "restart", "read", "read", "read", "read", "start" }, calls);
    }

    [Fact]
    public async Task OrdinaryStartDoesNotRestartOrPoll()
    {
        var result = await EncoderMonitorRecovery.StartAsync(() => Task.FromResult(Started),
            () => throw new Exception("Unexpected read"), _ => throw new Exception("Unexpected restart"),
            _ => { }, TestContext.Current.CancellationToken);
        Assert.False(result.Restarted);
        Assert.Equal(Started, result.Snapshot);
    }

    [Fact]
    public async Task AccessFailureDoesNotTriggerAnUpgradeRestart()
    {
        var failure = new EncoderSnapshot { ProcessId = 10, Supported = true, Message = "Access denied" };
        var result = await EncoderMonitorRecovery.StartAsync(() => Task.FromResult(failure),
            () => throw new Exception("Unexpected read"), _ => throw new Exception("Unexpected restart"),
            _ => { }, TestContext.Current.CancellationToken);
        Assert.False(result.Restarted);
        Assert.Equal(failure, result.Snapshot);
    }

    [Fact]
    public async Task PairedCodecNeverTriggersAnOrdinaryHelperRestart()
    {
        var paired = Old with { PairedCodecTrialPresent = true };
        var result = await EncoderMonitorRecovery.StartAsync(() => Task.FromResult(paired),
            () => throw new Exception("Unexpected read"), _ => throw new Exception("Unexpected restart"),
            _ => { }, TestContext.Current.CancellationToken);
        Assert.False(result.Restarted);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task ReplacementFailureOrPairedSessionDoesNotRestartAgain(bool restartRequired, bool paired, bool supported)
    {
        int starts = 0, restarts = 0;
        var replacement = Ready with { RequiresSteamVrRestart = restartRequired, PairedCodecTrialPresent = paired, Supported = supported };
        var result = await EncoderMonitorRecovery.StartAsync(
            () => { starts++; return Task.FromResult(Old); }, () => Task.FromResult(replacement),
            _ => { restarts++; return Task.CompletedTask; }, _ => { }, TestContext.Current.CancellationToken);
        Assert.True(result.Restarted);
        Assert.Equal(replacement, result.Snapshot);
        Assert.Equal(1, starts);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task StartupTimeoutIsBoundedAndNeverReloadsOldProcess()
    {
        int starts = 0, restarts = 0, reads = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => EncoderMonitorRecovery.StartAsync(
            () => { starts++; return Task.FromResult(Old); },
            () => { reads++; return Task.FromResult(Old); },
            _ => { restarts++; return Task.CompletedTask; }, _ => { }, TestContext.Current.CancellationToken,
            attempts: 3, wait: _ => Task.CompletedTask));
        Assert.Equal(1, starts);
        Assert.Equal(1, restarts);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task FailedRestartDoesNotLoadAnyHelper()
    {
        await Assert.ThrowsAsync<IOException>(() => EncoderMonitorRecovery.StartAsync(
            () => Task.FromResult(Old), () => throw new Exception("Unexpected read"),
            _ => throw new IOException("SteamVR could not stop"), _ => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationWhileWaitingNeverRetriesStart()
    {
        using var cancellation = new CancellationTokenSource();
        var starts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EncoderMonitorRecovery.StartAsync(
            () => { starts++; return Task.FromResult(Old); }, () => Task.FromResult(Old),
            _ => Task.CompletedTask, _ => { }, cancellation.Token,
            wait: _ => { cancellation.Cancel(); return Task.CompletedTask; }));
        Assert.Equal(1, starts);
    }
}
