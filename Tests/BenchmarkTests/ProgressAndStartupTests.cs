using SteamLinkVRSettings.Frame;

public class ProgressAndStartupTests
{
    private static readonly BenchmarkCandidate Candidate=new(150,EncoderMultiPass.SinglePass,false){Width=1664,Fps=90};
    [Fact] public async Task ClosingSampleWaitsForNewSceneTelemetry()
    {
        int reads=0;
        var scene=await BenchmarkSceneProcess.WaitForFreshAsync(()=>new(++reads<3?10:10.5,900,0,0,0,90),TestContext.Current.CancellationToken);
        Assert.Equal(10.5,scene.Seconds);Assert.Equal(3,reads);
    }
    [Fact] public async Task ClosingSampleDoesNotMaskSceneCounterReset()
    {
        int reads=0;
        await Assert.ThrowsAsync<IOException>(()=>BenchmarkSceneProcess.WaitForFreshAsync(
            ()=>new(++reads==1?10:1,0,0,0,0,90),TestContext.Current.CancellationToken));
    }
    [Fact] public async Task ClosingSampleCanBeCancelledWhileWaitingForTelemetry()
    {
        using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>BenchmarkSceneProcess.WaitForFreshAsync(
            ()=>new(10,900,0,0,0,90),stop.Token));
    }
    [Fact] public void MeasurementShowsRealSecondsAndModeCount()
    {
        var p=new BenchmarkProgressTracker();p.Plan(18,3,13,new());p.Screen(Candidate,10);p.Measuring(3.7,10);
        Assert.Equal("Mode test 1 / 18",p.Current.PhaseCount);Assert.Equal(3,p.Current.MeasuredSeconds);
        Assert.Equal(10,p.Current.MeasurementSeconds);Assert.Equal(69,p.Current.MaximumTests);
        Assert.Equal(0,p.Current.CompletedTests);Assert.InRange(p.Current.Percent,5,9);
        p.TrialFinished();Assert.Equal(1,p.Current.CompletedTests);
        p.Screen(Candidate,10);Assert.Equal("Mode test 2 / 18",p.Current.PhaseCount);Assert.False(p.Current.Measuring);
    }
    [Fact] public void ReconnectWaitDoesNotCountAsMeasurementOrCompletedTrial()
    {
        var p=new BenchmarkProgressTracker();p.Plan(18,3,13,new());p.Screen(Candidate,10);
        var percent=p.Current.Percent;p.Activity("Waiting for the wireless route (12 / 30 seconds)…");
        Assert.Equal(percent,p.Current.Percent);Assert.Equal(0,p.Current.MeasuredSeconds);Assert.Equal(0,p.Current.CompletedTests);
        Assert.False(p.Current.Measuring);Assert.Contains("Waiting",p.Current.Activity);
    }
    [Fact] public void InvalidShortWindowDoesNotClaimFullMeasurementDuration()
    {
        var p=new BenchmarkProgressTracker();p.Plan(18,3,13,new());p.Screen(Candidate,10);
        p.Measuring(3.1,10);p.TrialFinished(3.1);
        Assert.Equal(1,p.Current.CompletedTests);Assert.Equal(3,p.Current.MeasuredSeconds);
        Assert.Equal(10,p.Current.MeasurementSeconds);Assert.False(p.Current.Measuring);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void InterruptionNeverReportsOneHundredPercentOrUnfinishedPhasesAsComplete(bool recovery)
    {
        var p=new BenchmarkProgressTracker();p.Plan(18,3,13,new());p.Screen(Candidate,10);p.Measuring(5,10);
        var before=p.Current.Percent;p.Restoring(true);p.Finish(false,recovery,"Connection failed");
        Assert.Equal(before,p.Current.Percent);Assert.False(p.Current.Complete);Assert.True(p.Current.Terminal);
        Assert.Equal(1,p.Current.CompletedSteps);Assert.Equal(recovery,p.Current.RecoveryRequired);
    }
    [Fact] public async Task AdaptiveProgressIsMonotonicAndOnlyFinishesAfterRestoration()
    {
        var events=new List<BenchmarkProgressState>();var p=new BenchmarkProgressTracker(events.Add);
        var result=await BenchmarkExplorer.RunAsync(new(),450,false,(c,s,_)=>
        {
            p.Measuring(s/2d,s);
            var samples=Enumerable.Range(0,s+1).Select(i=>new BenchmarkSample(100+i,"stream",(ulong)(i*c.Fps),
                (ulong)(i*100_000_000d/8),0,0,0,0,0,2,new(200+i,"receiver",i*c.Fps,i*c.Fps,0,0),
                new(100+i,i*c.Fps,0,0,0,c.Fps),true,"Dedicated wireless"){PcTimeoutPresentations=0}).ToArray();
            return Task.FromResult(WirelessBenchmark.Evaluate(c,samples,c.Fps,s));
        },_=>{},TestContext.Current.CancellationToken,null,new(true,-20,-12),1664,90,p);
        Assert.Equal(result.Trials.Count,p.Current.CompletedTests);Assert.True(p.Current.CompletedTests<=p.Current.MaximumTests);
        Assert.Equal(95,p.Current.Percent);Assert.False(p.Current.Terminal);
        p.Restoring(false);Assert.Equal(95,p.Current.Percent);p.Finish(true,false,"Saved choices and restored settings");
        Assert.Equal(100,p.Current.Percent);Assert.True(p.Current.Complete);Assert.Equal(4,p.Current.CompletedSteps);
        Assert.All(events.Zip(events.Skip(1)),pair=>Assert.True(pair.Second.Percent>=pair.First.Percent));
    }
    [Fact] public async Task TransientStartupResumesSameHostOnceThenContinues()
    {
        int attempts=0,resumes=0;var messages=new List<string>();
        var result=await BenchmarkReceiverStartup.ConnectAsync(_=>++attempts==1?
            Task.FromException<int>(new BenchmarkReceiverStartupException("BenchmarkRouteNotReady: No active wireless transport yet",true)):Task.FromResult(42),
            _=>{resumes++;return Task.CompletedTask;},messages.Add,TestContext.Current.CancellationToken);
        Assert.Equal(42,result);Assert.Equal(2,attempts);Assert.Equal(1,resumes);Assert.Single(messages);
    }
    [Fact] public async Task PersistentStartupFailureIsBoundedAndKeepsItsReason()
    {
        int attempts=0,resumes=0;
        var error=await Assert.ThrowsAsync<IOException>(()=>BenchmarkReceiverStartup.ConnectAsync<int>(_=>
        {attempts++;throw new BenchmarkReceiverStartupException("No active wireless transport yet",true);},
            _=>{resumes++;return Task.CompletedTask;},_=>{},TestContext.Current.CancellationToken));
        Assert.Equal(2,attempts);Assert.Equal(1,resumes);Assert.Contains("one reconnect attempt",error.Message);
        Assert.Contains("No active wireless transport",error.Message);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task AmbiguousStartupAndMidTrialErrorsAreNotSilentlyRetried(bool midTrial)
    {
        int resumes=0;
        await Assert.ThrowsAnyAsync<IOException>(()=>BenchmarkReceiverStartup.ConnectAsync<int>(_=>
            Task.FromException<int>(midTrial?new IOException("Counter identity changed"):
                new BenchmarkReceiverStartupException("Multiple active transports: 2,3",false)),
            _=>{resumes++;return Task.CompletedTask;},_=>{},TestContext.Current.CancellationToken));
        Assert.Equal(0,resumes);
    }
    [Fact] public async Task CancelDuringStartupDoesNotReconnect()
    {
        using var stop=new CancellationTokenSource();int resumes=0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>BenchmarkReceiverStartup.ConnectAsync<int>(_=>
        {stop.Cancel();throw new BenchmarkReceiverStartupException("Not ready",true);},
            _=>{resumes++;return Task.CompletedTask;},_=>{},stop.Token));
        Assert.Equal(0,resumes);
    }
}
