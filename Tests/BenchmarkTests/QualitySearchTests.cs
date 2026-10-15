using SteamLinkVRSettings.Frame;

public class QualitySearchTests
{
    private static readonly BenchmarkCandidate Candidate=new(150,EncoderMultiPass.SinglePass,false,new(true,0,0)){Width=1664,Fps=90};
    private static BenchmarkTrial Trial(BenchmarkCandidate c,double smooth=100,bool serious=false,double mbps=100,double? qp=null)
        =>new(c,!serious,"Measured",10,mbps,c.Fps,c.Fps,2,[])
        {
            Assessment=new(true,serious,serious?100:0,serious?10:0,0,0,0,0,0,0,smooth,0,"Measured"),
            Quantizer=qp is {} value?new(value,900,10):null
        };

    [Theory][InlineData(true,-20,-12)][InlineData(true,3,15)][InlineData(false,-20,-12)]
    public async Task NeutralAndIndependentAxesAreAlwaysMeasuredWithoutRepeatingPairs(bool enabled,int gaze,int periphery)
    {
        var initial=new BenchmarkQuality(enabled,gaze,periphery);var observed=new List<BenchmarkTrial>();
        var best=await BenchmarkQualitySearch.RunAsync(Trial(Candidate),initial,new(),(c,stage)=>
        {var t=Trial(c) with {Stage=stage};observed.Add(t);return Task.FromResult(t);},TestContext.Current.CancellationToken);
        Assert.Contains(observed,t=>t.Candidate.Quality==new BenchmarkQuality(true,0,0));
        Assert.Equal(new[]{-20,-12,-6,6}.Append(enabled?gaze:0).Where(v=>v!=0).Distinct().Order(),
            observed.Where(t=>t.Stage.StartsWith("Gaze comparison")).Select(t=>t.Candidate.Quality!.GazeOffset).Order());
        Assert.Equal(new[]{-12,-8,-4,8}.Append(enabled?periphery:0).Where(v=>v!=0).Distinct().Order(),
            observed.Where(t=>t.Stage.StartsWith("Periphery comparison")).Select(t=>t.Candidate.Quality!.PeripheralOffset).Order());
        Assert.All(observed.Where(t=>t.Stage.StartsWith("Gaze comparison")),t=>Assert.Equal(0,t.Candidate.Quality!.PeripheralOffset));
        Assert.All(observed.Where(t=>t.Stage.StartsWith("Periphery comparison")),t=>Assert.Equal(0,t.Candidate.Quality!.GazeOffset));
        if(enabled)Assert.Contains(observed,t=>t.Candidate.Quality==initial);
        Assert.Equal(observed.Count,observed.Select(t=>t.Candidate.Quality).Distinct().Count());
        Assert.InRange(observed.Count,9,BenchmarkQualitySearch.MaximumTrials(initial));Assert.Contains(best,observed);
        Assert.All(observed,t=>Assert.Equal(Candidate,t.Candidate with {Quality=Candidate.Quality}));
    }

    [Fact] public async Task GoodIndependentRegionsDoNotAuthorizeAnUnmeasuredOrOverloadedCombination()
    {
        var observed=new List<BenchmarkTrial>();
        var best=await BenchmarkQualitySearch.RunAsync(Trial(Candidate),new(true,0,0),new(),(c,stage)=>
        {
            var q=c.Quality!;bool overloaded=q.GazeOffset<=-12 && q.PeripheralOffset<=-8;
            var t=Trial(c,serious:overloaded) with {Stage=stage};observed.Add(t);return Task.FromResult(t);
        },TestContext.Current.CancellationToken);
        Assert.Contains(observed,t=>t.Candidate.Quality==new BenchmarkQuality(true,-20,-12) && BenchmarkExplorer.Serious(t,new()));
        Assert.Contains(best,observed);Assert.False(BenchmarkExplorer.Serious(best,new()));
        Assert.NotEqual(BenchmarkQualitySearch.Neutral,best.Candidate.Quality);
    }

    [Fact] public async Task UpdatedNeutralBaselineSupersedesAnOlderPerfectModeScreen()
    {
        var best=await BenchmarkQualitySearch.RunAsync(Trial(Candidate),new(true,0,0),new(),(c,_)=>
            Task.FromResult(Trial(c,serious:true)),TestContext.Current.CancellationToken);
        Assert.True(BenchmarkExplorer.Serious(best,new()));
    }

    [Fact] public async Task DisabledComparisonKeepsExactStartingOffsetsThroughoutSearch()
    {
        var initial=new BenchmarkQuality(true,3,15);
        var r=await BenchmarkExplorer.RunAsync(new(AllowQualityReduction:false,TestResolutions:false,TestRefreshRates:false),450,false,
            (c,s,_)=>Task.FromResult(Trial(c) with {Seconds=s}),_=>{},TestContext.Current.CancellationToken,null,initial,1664,90);
        Assert.All(r.Trials,t=>Assert.Equal(initial,t.Candidate.Quality));
        Assert.DoesNotContain(r.Trials,t=>t.Stage.Contains("comparison") || t.Stage.Contains("baseline"));
    }

    [Fact] public void HigherPayloadAloneCannotImproveRankingOrWinABitrateProbe()
    {
        var low=Trial(Candidate,mbps:100);var high=Trial(Candidate with {TargetMbps=450},mbps:430);
        Assert.Equal(BenchmarkExplorer.Score(low),BenchmarkExplorer.Score(high));
        Assert.False(BenchmarkExplorer.Prefer(high,low,new()));
        Assert.Equal(low,Assert.Single(BenchmarkExplorer.Leaders([high,low],new())));
        Assert.Equal(low,WirelessBenchmark.Rank([high,low]).First());
    }

    [Fact] public void BitrateProbeNeedsCompressionImprovementWithoutSeriousIssues()
    {
        var low=Trial(Candidate,qp:28);var high=Trial(Candidate with {TargetMbps=275},qp:26);
        Assert.True(BenchmarkExplorer.Prefer(high,low,new()));
        Assert.Equal(high,Assert.Single(BenchmarkExplorer.Leaders([low,high],new())));
        Assert.False(BenchmarkExplorer.Prefer(high with {Quantizer=new(27.5,900,10)},low,new()));
        Assert.False(BenchmarkExplorer.Prefer(Trial(high.Candidate,serious:true,qp:10),low,new()));
        Assert.False(BenchmarkExplorer.Prefer(Trial(high.Candidate with {Quality=new(true,6,8)},qp:10),low,new()));
    }

    [Fact] public void MoreRegionalDetailCanWinDespiteAMinorHitchButNeverAMajorOverload()
    {
        var low=Trial(Candidate);var detail=Trial(Candidate with {Quality=new(true,-12,-8)},smooth:98,mbps:90);
        Assert.True(BenchmarkExplorer.Prefer(detail,low,new()));
        Assert.False(BenchmarkExplorer.Prefer(Trial(detail.Candidate,serious:true),low,new()));
        var profile=WirelessBenchmark.Profile(BuiltInPresets.Create()[0].Profile,detail.Candidate,1664,90);
        Assert.True(profile.QualityEnabled);Assert.Equal(-12,profile.GazeOffset);Assert.Equal(-8,profile.PeripheralOffset);
    }

    private static EncoderSnapshot Snapshot=>new(){Generation="session",TimestampSeconds=100,Codec="HEVC",BitDepth=10,
        EncodedFrames=1000,QuantizerSamples=1000,QuantizerSum=30000,Enabled=true,EncodingPolicy=new(){Width=1664,Height=6656}};
    private static EncoderSnapshot End=>Snapshot with {TimestampSeconds=110,EncodedFrames=1900,QuantizerSamples=1900,QuantizerSum=52500};
    [Fact] public void QpUsesCounterDeltasRatherThanAnEarlierSessionAverage()
    {
        var q=BenchmarkQuantizer.Between(Snapshot,End);Assert.NotNull(q);Assert.Equal(25,q.MeanQp);Assert.Equal(900ul,q.Frames);Assert.Equal(10,q.Seconds);
    }
    [Theory][InlineData("generation")][InlineData("map")][InlineData("width")][InlineData("reset")][InlineData("missing")]
    [InlineData("impossible")][InlineData("time")][InlineData("errors")][InlineData("count")]
    public void QpDoesNotInventDataWhenItsWindowIsUnreliable(string change)
    {
        var end=change switch
        {
            "generation"=>End with {Generation="other"},"map"=>End with {CenterOffset=-12},
            "width"=>End with {EncodingPolicy=new(){Width=2048,Height=8192}},"reset"=>End with {QuantizerSum=10},
            "missing"=>End with {QuantizerSamples=1000},"impossible"=>End with {QuantizerSum=900000},
            "time"=>End with {TimestampSeconds=double.NaN},"errors"=>End with {RejectedApiCalls=1},
            _=>End with {QuantizerSamples=2000}
        };
        Assert.Null(BenchmarkQuantizer.Between(Snapshot,end));
    }
}
