using SteamLinkVRSettings.Frame;

public sealed class DisplayRecoveryTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"display-recovery-"+Guid.NewGuid().ToString("N"));
    private BenchmarkDisplayLease Lease(Fake fake)=>new(new(root,root),new(),Path.Combine(root,"journal.json"),fake);
    [Fact] public async Task RestoresActualHzAndAbsentHostPreferenceAfterRestart()
    {
        var fake=new Fake();var lease=Lease(fake);await lease.BeginAsync(90,TestContext.Current.CancellationToken);
        await lease.ApplyAsync(120,TestContext.Current.CancellationToken);fake.PcHz=120;
        int restarts=0;await Lease(fake).RestoreAsync(()=>{restarts++;fake.PcHz=90;return Task.CompletedTask;},TestContext.Current.CancellationToken);
        Assert.Equal(1,restarts);Assert.Null(fake.HostHz);Assert.Equal(0,fake.Preferred);Assert.Equal(90,fake.Hz);Assert.False(lease.Pending);
    }
    [Fact] public async Task PartialFrameWriteRetainsRecoveryAndCanBeRetried()
    {
        var fake=new Fake();var lease=Lease(fake);await lease.BeginAsync(90,TestContext.Current.CancellationToken);fake.FailFrame=true;
        await Assert.ThrowsAsync<IOException>(()=>lease.ApplyAsync(120,TestContext.Current.CancellationToken));
        Assert.True(lease.Pending);fake.FailFrame=false;
        await Lease(fake).RestoreAsync(()=>Task.CompletedTask,TestContext.Current.CancellationToken);
        Assert.Null(fake.HostHz);Assert.Equal(0,fake.Preferred);Assert.False(lease.Pending);
    }
    [Fact] public async Task FailedVerificationKeepsJournalUntilActualModeIsRecovered()
    {
        var fake=new Fake();var lease=Lease(fake);await lease.BeginAsync(90,TestContext.Current.CancellationToken);
        await lease.ApplyAsync(120,TestContext.Current.CancellationToken);fake.PcHz=120;
        await Assert.ThrowsAsync<IOException>(()=>lease.RestoreAsync(()=>Task.CompletedTask,TestContext.Current.CancellationToken));
        Assert.True(lease.Pending);fake.PcHz=90;
        await Lease(fake).RestoreAsync(()=>Task.CompletedTask,TestContext.Current.CancellationToken);Assert.False(lease.Pending);
    }
    [Fact] public async Task AnotherControllerChangingPreferencesIsPreserved()
    {
        var fake=new Fake();var lease=Lease(fake);await lease.BeginAsync(90,TestContext.Current.CancellationToken);
        await lease.ApplyAsync(120,TestContext.Current.CancellationToken);fake.HostHz=144;fake.Preferred=144;
        await lease.RestoreAsync(()=>throw new Exception("must not restart"),TestContext.Current.CancellationToken);
        Assert.Equal(144,fake.HostHz);Assert.Equal(144,fake.Preferred);
    }
    [Fact] public async Task SecondInstanceCannotTakeOverOrRestoreAnotherTestsJournal()
    {
        var fake=new Fake();var lease=Lease(fake);await lease.BeginAsync(90,TestContext.Current.CancellationToken);
        var second=Lease(fake);await Assert.ThrowsAsync<IOException>(()=>second.BeginAsync(90,TestContext.Current.CancellationToken));
        await second.RestoreAsync(()=>Task.CompletedTask,TestContext.Current.CancellationToken);Assert.True(lease.Pending);
    }
    [Fact] public async Task NativeRefreshRoundingDoesNotLookLikeAnExternalEdit()
    {
        var fake=new Fake();var lease=Lease(fake);await lease.BeginAsync(90,TestContext.Current.CancellationToken);
        await lease.ApplyAsync(120,TestContext.Current.CancellationToken);
        fake.HostHz=120.001;fake.Preferred=120.0000076;fake.Hz=120.0000076;
        await lease.ApplyAsync(90,TestContext.Current.CancellationToken);
        fake.HostHz=90.0009;fake.Preferred=90.0000076;fake.Hz=90.0000076;
        await lease.ApplyAsync(120,TestContext.Current.CancellationToken);
        fake.HostHz=120.001;fake.Preferred=120.0000076;
        await lease.RestoreAsync(()=>Task.CompletedTask,TestContext.Current.CancellationToken);
        Assert.Null(fake.HostHz);Assert.Equal(0,fake.Preferred);Assert.Equal(90,fake.Hz);Assert.False(lease.Pending);
    }
    [Fact] public async Task SteamVrRemovingAnExplicitDefaultDoesNotInterruptTheNextSweep()
    {
        var fake=new Fake{HostHz=90};var lease=Lease(fake);await lease.BeginAsync(90,TestContext.Current.CancellationToken);
        await lease.ApplyAsync(120,TestContext.Current.CancellationToken);
        await lease.ApplyAsync(90,TestContext.Current.CancellationToken);fake.HostHz=null;
        await lease.ApplyAsync(120,TestContext.Current.CancellationToken);
        Assert.Equal(120,fake.Hz);
        await lease.RestoreAsync(()=>Task.CompletedTask,TestContext.Current.CancellationToken);
        Assert.Equal(90,fake.HostHz);Assert.Equal(90,fake.Hz);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    private sealed class Fake:IBenchmarkDisplay
    {
        public double? HostHz;public double? Preferred=0;public double Hz=90,PcHz=90;public bool FailFrame;
        public Task<double?> Host()=>Task.FromResult(HostHz);
        public Task<double> EffectiveHost()=>Task.FromResult(HostHz??90);
        public Task<FrameDisplayRate.State> Frame(CancellationToken token)=>Task.FromResult(new FrameDisplayRate.State(Hz,Preferred));
        public Task Host(double? hz){HostHz=hz;return Task.CompletedTask;}
        public Task Frame(int hz,CancellationToken token){if(FailFrame)throw new IOException("Disconnected");Hz=hz;Preferred=hz;return Task.CompletedTask;}
        public Task Preference(double? hz,CancellationToken token){Preferred=hz;return Task.CompletedTask;}
        public Task<double> Pc()=>Task.FromResult(PcHz);
    }
}
