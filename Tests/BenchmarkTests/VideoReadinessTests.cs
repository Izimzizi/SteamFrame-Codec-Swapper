using SteamLinkVRSettings.Frame;

public class VideoReadinessTests
{
    private static EncoderSnapshot Video(ulong frame) => new()
    {
        ProcessId=7, Generation="stream", Installed=true, Active=true, Codec="HEVC", CodecObserved=true,
        EncodedFrames=frame, EncodedBytes=frame*1000, TimestampSeconds=frame, LastEncodedTimestampSeconds=frame
    };
    private static Task NoWait(CancellationToken ct) {ct.ThrowIfCancellationRequested();return Task.CompletedTask;}

    [Fact] public async Task FreshVideoDoesNotRestartTheRuntime()
    {
        ulong frame=1;int restarts=0;
        await new BenchmarkVideoReadiness().EnsureAsync(()=>Task.FromResult(Video(frame++)),
            _=>{restarts++;return Task.CompletedTask;},_=>{},TestContext.Current.CancellationToken,NoWait);
        Assert.Equal(0,restarts);
    }
    [Fact] public async Task StoppedVideoRecoversBeforeTheNextMeasurement()
    {
        ulong frame=1;bool resumed=false;int restarts=0;
        await new BenchmarkVideoReadiness().EnsureAsync(()=>Task.FromResult(Video(resumed?frame++:frame)),
            _=>{restarts++;resumed=true;return Task.CompletedTask;},_=>{},TestContext.Current.CancellationToken,NoWait);
        Assert.Equal(1,restarts);Assert.True(frame>2);
    }
    [Fact] public async Task PersistentMissingOutputDoesNotLoopOrClaimReadiness()
    {
        int restarts=0;
        var error=await Assert.ThrowsAsync<IOException>(()=>new BenchmarkVideoReadiness().EnsureAsync(()=>Task.FromResult(Video(1)),
            _=>{restarts++;return Task.CompletedTask;},_=>{},TestContext.Current.CancellationToken,NoWait));
        Assert.Equal(1,restarts);Assert.Contains("did not resume",error.Message);
    }
    [Fact] public async Task RecoveryBudgetIsSharedAcrossTrials()
    {
        var readiness=new BenchmarkVideoReadiness();int restarts=0;bool resumed=false;ulong frame=1;
        Task Ensure()=>readiness.EnsureAsync(()=>Task.FromResult(Video(resumed?frame++:frame)),
            _=>{restarts++;resumed=true;return Task.CompletedTask;},_=>{},TestContext.Current.CancellationToken,NoWait);
        await Ensure();resumed=false;await Ensure();resumed=false;
        var error=await Assert.ThrowsAsync<IOException>(Ensure);
        Assert.Equal(2,restarts);Assert.Contains("two automatic recovery",error.Message);
    }
    [Fact] public async Task OwnershipFailureIsNotTreatedAsADroppedLink()
    {
        int restarts=0;
        await Assert.ThrowsAsync<IOException>(()=>new BenchmarkVideoReadiness().EnsureAsync(
            ()=>throw new IOException("Another controller changed the encoder"),
            _=>{restarts++;return Task.CompletedTask;},_=>{},TestContext.Current.CancellationToken,NoWait));
        Assert.Equal(0,restarts);
    }
    [Fact] public async Task CancellationDuringReadinessDoesNotStartRecovery()
    {
        using var stop=new CancellationTokenSource();int restarts=0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new BenchmarkVideoReadiness().EnsureAsync(()=>Task.FromResult(Video(1)),
            _=>{restarts++;return Task.CompletedTask;},_=>{},stop.Token,ct=>{stop.Cancel();return NoWait(ct);}));
        Assert.Equal(0,restarts);
    }
    [Fact] public async Task StaleReceiverIsStoppedBeforeResumingTheVerifiedHost()
    {
        var calls=new List<string>();
        Task Call(string name){calls.Add(name);return Task.CompletedTask;}
        await BenchmarkHostConnection.RestartClientAsync(_=>Call("validate"),_=>Call("stop"),_=>Call("start"),
            TestContext.Current.CancellationToken,_=>Call("settle"));
        Assert.Equal(new[]{"validate","stop","settle","validate","start"},calls);
    }
    [Theory][InlineData(1)][InlineData(2)]
    public async Task AnotherHostIsNeverStoppedOrStarted(int failedValidation)
    {
        int validations=0,stops=0,starts=0;
        await Assert.ThrowsAsync<IOException>(()=>BenchmarkHostConnection.RestartClientAsync(_=>
            {if(++validations==failedValidation)throw new IOException("Another host is active");return Task.CompletedTask;},
            _=>{stops++;return Task.CompletedTask;},_=>{starts++;return Task.CompletedTask;},TestContext.Current.CancellationToken,NoWait));
        Assert.Equal(failedValidation-1,stops);Assert.Equal(0,starts);
    }
}
