using System.Text.Json;
using SteamLinkVRSettings.Frame;

public class WirelessTuningTests
{
    private static readonly BenchmarkCandidate Candidate = new(300,EncoderMultiPass.QuarterResolution,false);
    private static List<BenchmarkSample> Good(int seconds=25,int fps=90,double mbps=240) => Enumerable.Range(0,seconds+1).Select(i=>new BenchmarkSample(
        100+i,"session",(ulong)(i*fps),(ulong)(i*mbps*1e6/8),0,0,0,0,0,2,
        new(1000+i,"receiver",i*fps,i*fps,0,0),new(i+1,i*fps,0,0,0,fps),true,"Dedicated wireless") {PcTimeoutPresentations=0}).ToList();
    private static BenchmarkTrial Trial(BenchmarkCandidate c,int seconds,double mbps=100) => WirelessBenchmark.Evaluate(c,Good(seconds,fps:c.Fps>0?c.Fps:90,mbps:mbps),c.Fps>0?c.Fps:90,seconds);
    private static List<BenchmarkSample> Hitches(List<BenchmarkSample> samples,params (int At,int Count)[] events)
        => samples.Select((s,i)=>s with {Receiver=s.Receiver with {Timeouts=events.Where(e=>i>=e.At).Sum(e=>e.Count)}}).ToList();

    [Fact] public void SliderTargetIsNotMeasuredBitrate()
    { var t=Trial(Candidate,25,240);Assert.True(t.Passed);Assert.Equal(240,t.ActualMbps);Assert.Equal(90,t.ReceiverFps); }
    [Fact] public void OneTimeoutIn197SecondsIsUsableAndRetained()
    {
        var t=WirelessBenchmark.Evaluate(Candidate,Hitches(Good(300),(197,1)),90,300);
        Assert.True(t.Passed);Assert.True(t.MeasurementValid);Assert.Equal(300,t.Seconds);
        Assert.Equal(1,t.Assessment!.AffectedFrameEstimate);Assert.Contains("occasional",t.Assessment.Description);
    }
    [Fact] public void OnePcDropAfter84SecondsIsNotAnAutomaticFailure()
    {
        var rows=Good(120).Select((s,i)=>s with {PcDrops=i>=84?1:0}).ToArray();
        Assert.True(WirelessBenchmark.Evaluate(Candidate,rows,90,120).Passed);
    }
    [Fact] public void TimeoutPresentationsAreSeparateFromTimeoutStateEntries()
    {
        var rows=Good().Select((s,i)=>s with {PcTimeouts=i>=12?1:0}).ToList();
        var t=WirelessBenchmark.Evaluate(Candidate,rows,90,25);Assert.Equal(0,t.Assessment!.AffectedFrameEstimate);
        rows=rows.Select((s,i)=>s with {PcTimeoutPresentations=i>=12?3:0}).ToList();
        t=WirelessBenchmark.Evaluate(Candidate,rows,90,25);Assert.True(t.Passed);Assert.Equal(3,t.Assessment!.AffectedFrameEstimate);
    }
    [Fact] public void CorrelatedPcAndReceiverCountersAreNotAddedTogether()
    {
        var rows=Hitches(Good(),(12,3)).Select((s,i)=>s with {PcDrops=i>=12?3:0,PcTimeoutPresentations=i>=12?3:0}).ToArray();
        Assert.Equal(3,WirelessBenchmark.Evaluate(Candidate,rows,90,25).Assessment!.AffectedFrameEstimate);
    }
    [Fact] public void SameCountConcentratedInABurstScoresWorseThanIsolatedHitches()
    {
        var isolated=WirelessBenchmark.Evaluate(Candidate,Hitches(Good(300),Enumerable.Range(1,10).Select(i=>(i*25,1)).ToArray()),90,300);
        var burst=WirelessBenchmark.Evaluate(Candidate,Hitches(Good(300),(100,10)),90,300);
        Assert.Equal(isolated.Assessment!.AffectedFrameEstimate,burst.Assessment!.AffectedFrameEstimate);
        Assert.True(isolated.Assessment.SmoothnessScore>burst.Assessment.SmoothnessScore);
    }
    [Fact] public void ALongFreezeIsMajorButRemainsAValidScoredMeasurement()
    {
        var rows=Good().Select((s,i)=>i==12?s with {Frames=(ulong)(11*90),Bytes=11*30_000_000}:s).ToArray();
        var t=WirelessBenchmark.Evaluate(Candidate,rows,90,25);
        Assert.True(t.MeasurementValid);Assert.False(t.Passed);Assert.True(t.Assessment!.MajorIssues);
        Assert.Equal(1,t.Assessment.ObservedNoDeliverySeconds);Assert.Equal(25,t.Seconds);
    }
    [Fact] public void SustainedLowDeliveryIsMajorEvenWithoutTimeoutCounters()
    {
        var rows=Good().Select((s,i)=>s with {Receiver=s.Receiver with {Submits=i*70}}).ToArray();
        var t=WirelessBenchmark.Evaluate(Candidate,rows,90,25);Assert.True(t.MeasurementValid);Assert.True(t.Assessment!.MajorIssues);
    }
    [Theory]
    [InlineData("gap")][InlineData("tracking")][InlineData("submit")][InlineData("generation")][InlineData("receiver")]
    [InlineData("pending")][InlineData("wired")][InlineData("nan")][InlineData("error")][InlineData("rollback")]
    public void MissingOrInvalidEvidenceDoesNotSupportAnImprovement(string failure)
    {
        var rows=Good();var s=rows[12];rows[12]=failure switch
        {
            "gap"=>s with {HistoryGaps=1},"tracking"=>s with {Scene=s.Scene with {Tracked=false}},
            "submit"=>s with {Scene=s.Scene with {SubmitErrors=1}},"generation"=>s with {Identity="new"},
            "receiver"=>s with {Receiver=s.Receiver with {Identity="new"}},"pending"=>s with {ConfigurationAccepted=false},
            "wired"=>s with {WirelessPath="USB cable"},"error"=>s with {Errors=1},
            "rollback"=>s with {PcTimeoutPresentations=-1},_=>s with {GpuP99Ms=double.NaN}
        };
        Assert.False(WirelessBenchmark.Evaluate(Candidate,rows,90,25).MeasurementValid);
    }
    [Fact] public void UnobservedHistoryStillInvalidatesAWindow()
    { var rows=Good().Select((s,i)=>s with {HistoryGaps=i>=12?1:0}).ToArray();Assert.False(WirelessBenchmark.Evaluate(Candidate,rows,90,25).MeasurementValid); }
    [Fact] public void HzNormalizesAffectedPercentageAndGpuBudget()
    {
        var a=WirelessBenchmark.Evaluate(Candidate,Hitches(Good(120,90),(30,3)),90,120);
        var b=WirelessBenchmark.Evaluate(Candidate,Hitches(Good(120,120),(30,3)),120,120);
        Assert.True(b.Assessment!.AffectedPercent<a.Assessment!.AffectedPercent);
        var rows=Good(25,120).Select(s=>s with {GpuP99Ms=7}).ToArray();Assert.True(WirelessBenchmark.Evaluate(Candidate,rows,120,25).Assessment!.MajorIssues);
        Assert.False(WirelessBenchmark.Evaluate(Candidate,rows,90,25).MeasurementValid);
    }
    [Fact] public void RefreshChangesAndIncompleteOrStaleWindowsRemainInvalid()
    {
        var rows=Good();rows[12]=rows[12] with {Scene=rows[12].Scene with {Fps=120}};
        Assert.False(WirelessBenchmark.Evaluate(Candidate,rows,90,25).MeasurementValid);
        Assert.False(WirelessBenchmark.Evaluate(Candidate,Good(10),90,25).MeasurementValid);
        Assert.False(WirelessBenchmark.Evaluate(Candidate,Good().Select(s=>s with {Receiver=Good()[0].Receiver}).ToArray(),90,25).MeasurementValid);
    }
    [Fact] public void UnevenSceneSpacingAloneDoesNotMeanAVisibleHitch()
    {
        var rows=Good().Select((s,i)=>s with {Scene=s.Scene with {LongIntervals=i>=12?1:0}}).ToArray();
        var t=WirelessBenchmark.Evaluate(Candidate,rows,90,25);Assert.True(t.Passed);Assert.Equal(0,t.Assessment!.AffectedFrameEstimate);Assert.NotNull(t.SceneTimingNote);
    }
    [Fact] public void RankingBalancesRegionalDetailAgainstMinorHitches()
    {
        var low=Trial(Candidate with {TargetMbps=50},120,40);
        var high=WirelessBenchmark.Evaluate(Candidate with {Quality=new(true,-12,-4)},Hitches(Good(120,mbps:240),(84,1)),90,120);
        Assert.Equal(high,WirelessBenchmark.Rank([low,high]).First());
        var severe=WirelessBenchmark.Evaluate(Candidate,Hitches(Good(120,mbps:400),(20,90)),90,120);
        Assert.Equal(high,WirelessBenchmark.Rank([severe,high]).First());
    }
    [Fact] public void ConnectedFreezeRemainsMeasurableInsteadOfLookingLikeADisconnect()
    {
        var stalled=new EncoderSnapshot {Generation="stream",ProcessId=37,Installed=true,Codec="HEVC",CodecObserved=true,
            Active=false,TimestampSeconds=50,LastEncodedTimestampSeconds=45};
        Assert.True(BenchmarkRunner.MeasurementStreamMatches(stalled,"stream",37));
        Assert.False(BenchmarkRunner.MeasurementStreamMatches(stalled,"replacement",37));
        Assert.False(BenchmarkRunner.MeasurementStreamMatches(stalled,"stream",38));
        Assert.False(BenchmarkRunner.MeasurementStreamMatches(stalled with {CodecReadFailed=true},"stream",37));
    }
    [Fact] public void RankingDoesNotRewardAnInflatedSliderWithIdenticalPayload()
    {
        var a=Trial(Candidate,25,240);var b=Trial(Candidate with {TargetMbps=500},25,240);
        Assert.Equal(a,WirelessBenchmark.Rank([b,a]).First());
    }
    [Fact] public async Task SearchCoversModesAndPassesWithoutAValveGate()
    {
        var r=await WirelessBenchmark.RunAsync(new(),500,false,(c,s,_)=>Task.FromResult(Trial(c,s,c.TargetMbps*.8)),_=>{},TestContext.Current.CancellationToken);
        var screens=r.Trials.Where(t=>t.Stage=="Mode/pass screen").ToArray();
        Assert.Equal(18,screens.Length);Assert.Equal(new[]{1280,1664,2048},screens.Select(t=>t.Candidate.Width).Distinct().Order().ToArray());
        Assert.Equal(new[]{90,120},screens.Select(t=>t.Candidate.Fps).Distinct().Order().ToArray());
        Assert.Equal(3,screens.Select(t=>t.Candidate.MultiPass).Distinct().Count());Assert.DoesNotContain(r.Trials,t=>t.Candidate.ValveReference);
        Assert.Equal(3,r.Recommendations.Count);Assert.All(r.Recommendations,r=>Assert.True(r.Measured));
        Assert.All(r.Recommendations,r=>Assert.Equal("Confirmation",r.Trial!.Stage));
        Assert.InRange(r.Trials.Count,45,69);Assert.DoesNotContain("Valve defaults",r.Message);
        Assert.All(screens,t=>Assert.False(t.Candidate.FreshSession));
        Assert.All(r.Trials.Where(t=>t.Stage=="Confirmation"),t=>Assert.True(t.Candidate.FreshSession));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task BadConfirmationGetsOneMeasuredAlternativeWithoutDiscardingEvidence(bool invalid)
    {
        var c=new BenchmarkCandidate(100,EncoderMultiPass.QuarterResolution,false){Width=1280,Fps=90};
        var seeds=new List<BenchmarkTrial>{Trial(c,10,100),Trial(c with {MultiPass=EncoderMultiPass.SinglePass},10,90)};
        int calls=0;
        var result=await BenchmarkExplorer.RefineAsync(seeds,new(MaximumMbps:100,AllowQualityReduction:false),100,false,(candidate,seconds,_)=>
        {
            calls++;Assert.True(candidate.FreshSession);
            var rows=candidate.MultiPass==EncoderMultiPass.SinglePass?Good(seconds,90,90):
                invalid?Good(3):Hitches(Good(seconds),Enumerable.Range(1,seconds).Select(i=>(i,12)).ToArray());
            return Task.FromResult(WirelessBenchmark.Evaluate(candidate,rows,90,seconds));
        },_=>{},TestContext.Current.CancellationToken,null,null,1280,90);
        Assert.Equal(2,calls);Assert.Equal(4,result.Trials.Count);
        var choice=Assert.Single(result.Recommendations);
        Assert.Equal(EncoderMultiPass.SinglePass,choice.Candidate.MultiPass);
        Assert.DoesNotContain("Above your",choice.Note);
        Assert.Contains(result.Trials,t=>!t.MeasurementValid || BenchmarkExplorer.Serious(t,new()));
    }
    [Fact] public void DetailAndRefreshChoicesExploreTheirModesBeforeMinorScoreDifferences()
    {
        BenchmarkTrial Mode(int width,int fps,double smooth)
        {
            var t=Trial(Candidate with {Width=width,Fps=fps},10);
            return t with {Assessment=t.Assessment! with {SmoothnessScore=smooth}};
        }
        var leaders=BenchmarkExplorer.Leaders([Mode(1280,90,100),Mode(1664,90,65),Mode(2048,90,60),Mode(1280,120,60)],new());
        Assert.Equal((1280,90),(leaders[0].Candidate.Width,leaders[0].Candidate.Fps));
        Assert.Equal(2048,leaders[1].Candidate.Width);Assert.Equal(120,leaders[2].Candidate.Fps);
    }
    [Fact] public void PoorConfirmationDoesNotHideGoodUnconfirmedModes()
    {
        var fast=Candidate with {Width=1280,Fps=120};var slow=Candidate with {Width=1280,Fps=90};
        var screen=Trial(fast,10) with {Stage="Mode/pass screen"};
        var confirmed=WirelessBenchmark.Evaluate(fast,Hitches(Good(35,120),Enumerable.Range(1,35).Select(i=>(i,20)).ToArray()),120,35) with {Stage="Confirmation"};
        var other=Trial(slow,10) with {Stage="Mode/pass screen"};
        var result=BenchmarkExplorer.Recommend([screen,confirmed,other],new(),false);
        Assert.Equal(other,result.Winner);Assert.Contains("Screened only",result.Recommendations[0].Note);
        Assert.Contains(result.Recommendations,r=>r.Trial==confirmed);Assert.DoesNotContain(result.Recommendations,r=>r.Trial==screen);
    }
    [Fact] public void FreshSessionIsNotADifferentPresetWhenComparingRepeatedObservations()
    {
        var c=Candidate with {Width=1664,Fps=90};
        var good=Trial(c,35) with {Stage="Confirmation"};
        var worse=WirelessBenchmark.Evaluate(c with {FreshSession=true},Hitches(Good(35),(10,10)),90,35) with {Stage="Confirmation"};
        Assert.Equal(worse,Assert.Single(BenchmarkExplorer.Leaders([good,worse],new())));
    }
    [Fact] public void AlternativeNamesDoNotPromiseMorePixelsOrHzThanBalanced()
    {
        var trials=new[]{(1664,120),(1664,90),(1280,90)}.Select(m=>Trial(Candidate with {Width=m.Item1,Fps=m.Item2},35) with {Stage="Confirmation"}).ToArray();
        var choices=BenchmarkExplorer.Recommend(trials,new(),false).Recommendations;
        Assert.Equal("Balanced",choices[0].Name);Assert.Equal(120,choices[0].Candidate.Fps);
        Assert.Contains(choices,c=>c.Name=="Lower refresh");Assert.Contains(choices,c=>c.Name=="Lower resolution");
        Assert.DoesNotContain(choices,c=>c.Name is "Higher detail" or "Higher refresh");
    }
    [Fact] public async Task SavedChoicesUseTheirExactlyMeasuredHeadroomModes()
    {
        var r=await WirelessBenchmark.RunAsync(new(MaximumMbps:200),232,false,(c,s,_)=>Task.FromResult(Trial(c,s,c.TargetMbps*.8)),_=>{},TestContext.Current.CancellationToken);
        Assert.All(r.Recommendations,choice=>
        {
            Assert.Equal(35,choice.Trial!.Seconds);Assert.Equal(choice.Candidate,choice.Trial.Candidate);
            var p=WirelessBenchmark.Profile(BuiltInPresets.Create()[0].Profile,choice.Candidate,choice.Candidate.Width,choice.Candidate.Fps);
            Assert.Equal(choice.Candidate.Fps,p.Fps);Assert.Equal(p.Fps,p.Video["preferredRefreshRate"].GetDouble());
            Assert.Equal(choice.Candidate.Width,p.Driver["streamFormatWidth"].GetInt32());
            Assert.Equal(choice.Candidate.MultiPass,p.MultiPass);Assert.InRange(p.TargetMbps,25,180);
        });
    }
    [Fact] public async Task AllPoorTrialsStillProduceExplicitMeasuredChoicesWithWarnings()
    {
        var r=await WirelessBenchmark.RunAsync(new(MaximumMbps:100),500,false,(c,s,_)=>Task.FromResult(
            WirelessBenchmark.Evaluate(c,Hitches(Good(s,c.Fps),Enumerable.Range(1,s).Select(i=>(i,12)).ToArray()),c.Fps,s)),_=>{},TestContext.Current.CancellationToken);
        Assert.NotEmpty(r.Recommendations);Assert.All(r.Recommendations,r=>Assert.Contains("Above your serious-issue threshold",r.Note));
        Assert.All(r.Recommendations,r=>Assert.True(r.Measured));Assert.DoesNotContain("Valve defaults",r.Message);
    }
    [Fact] public async Task InvalidMeasurementsProduceHonestUnmeasuredSettings()
    {
        var r=await WirelessBenchmark.RunAsync(new(),500,false,(c,s,_)=>Task.FromResult(WirelessBenchmark.Evaluate(c,Good(3,c.Fps),c.Fps,s)),_=>{},TestContext.Current.CancellationToken);
        var choice=Assert.Single(r.Recommendations);Assert.False(choice.Measured);Assert.Null(choice.Trial);
        Assert.Equal(1280,choice.Candidate.Width);Assert.Equal(90,choice.Candidate.Fps);Assert.Equal(100,choice.Candidate.TargetMbps);
        Assert.DoesNotContain("Valve defaults",r.Message);Assert.Contains("untested",choice.Note);
    }
    [Fact] public async Task SmallHitchesNeverSuppressARecommendation()
    {
        var r=await WirelessBenchmark.RunAsync(new(MaximumMbps:150),500,false,(c,s,_)=>Task.FromResult(
            WirelessBenchmark.Evaluate(c,Hitches(Good(s,c.Fps,c.TargetMbps*.8),(5,1)),c.Fps,s)),_=>{},TestContext.Current.CancellationToken);
        Assert.Equal(3,r.Recommendations.Count);Assert.All(r.Recommendations,r=>Assert.Equal(1,r.Trial!.Assessment!.AffectedFrameEstimate));
    }
    [Fact] public void SeriousIssueThresholdIsConfigurableWithoutDiscardingTheTrial()
    {
        var candidate=Candidate with{Width=1664,Fps=90};
        var t=WirelessBenchmark.Evaluate(candidate,Hitches(Good(30),Enumerable.Range(1,30).Select(i=>(i,3)).ToArray()),90,30);
        Assert.True(BenchmarkExplorer.Serious(t,new(IssuePercent:2)));Assert.False(BenchmarkExplorer.Serious(t,new(IssuePercent:5)));
        Assert.True(t.MeasurementValid);Assert.NotEmpty(BenchmarkExplorer.Recommend([t],new(IssuePercent:2),false).Recommendations);
    }
    [Fact] public void DisabledModeComparisonsPreserveActualStartingMode()
    {
        Assert.Equal(new[]{(1536,120)},BenchmarkExplorer.Modes(new(TestResolutions:false,TestRefreshRates:false),1536,120));
        Assert.Contains((1536,120),BenchmarkExplorer.Modes(new(),1536,120));
    }
    [Fact] public void UnmeasuredChoicePreservesExplicitlyFixedModes()
    {
        var choice=Assert.Single(BenchmarkExplorer.Recommend([],new(TestResolutions:false,TestRefreshRates:false),false,
            "Headset unavailable",1536,120).Recommendations);
        Assert.Equal(1536,choice.Candidate.Width);Assert.Equal(120,choice.Candidate.Fps);Assert.False(choice.Measured);
    }
    [Fact] public async Task RefinementKeepsTheLowerLoadChoiceWhenHigherTargetsCrossTheLimit()
    {
        var result=await WirelessBenchmark.RunAsync(new(),500,false,(c,s,_)=>Task.FromResult(
            WirelessBenchmark.Evaluate(c,c.TargetMbps>250?Hitches(Good(s,c.Fps,c.TargetMbps),Enumerable.Range(1,s).Select(i=>(i,12)).ToArray()):
                Good(s,c.Fps,c.TargetMbps),c.Fps,s)),_=>{},TestContext.Current.CancellationToken);
        Assert.Contains(result.Trials,t=>t.Candidate.TargetMbps>250);
        Assert.All(result.Recommendations,r=>Assert.True(r.Candidate.TargetMbps<=225));
        Assert.All(result.Recommendations,r=>Assert.DoesNotContain("Above your",r.Note));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SenderModeIsRetainedThroughoutReferenceAndSearch(bool mode)
    {
        var r=await WirelessBenchmark.RunAsync(new(MaximumMbps:100),500,mode,(c,s,_)=>Task.FromResult(Trial(c,s,c.ValveReference?30:c.TargetMbps*.8)),_=>{},TestContext.Current.CancellationToken);
        Assert.All(r.Trials,t=>Assert.Equal(mode,t.Candidate.AsyncSend));
    }
    [Fact] public async Task CancellationStillStopsAndDoesNotInventACompletedResult()
    {
        using var stop=new CancellationTokenSource();int calls=0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>WirelessBenchmark.RunAsync(new(),500,false,(c,s,_)=>
        {calls++;stop.Cancel();return Task.FromResult(Trial(c,s));},_=>{},stop.Token));Assert.Equal(1,calls);
    }
    [Fact] public async Task QualityAlternativesOnlyRefineLeadingModes()
    {
        var initial=new BenchmarkQuality(true,-20,-12);
        var r=await WirelessBenchmark.RunAsync(new(MaximumMbps:100),500,false,(c,s,_)=>Task.FromResult(Trial(c,s,c.TargetMbps*.8)),_=>{},TestContext.Current.CancellationToken,initialQuality:initial);
        Assert.All(r.Trials.Where(t=>t.Stage=="Mode/pass screen"),t=>Assert.Equal(BenchmarkQualitySearch.Neutral,t.Candidate.Quality));
        Assert.Equal(3,r.Trials.Count(t=>t.Stage=="Quality baseline 0 / 0"));
        Assert.Equal(3,r.Trials.Where(t=>t.Stage.StartsWith("Gaze comparison")).Select(t=>(t.Candidate.Width,t.Candidate.Fps)).Distinct().Count());
    }
    [Fact] public void PresetRetainsTestedQualityAndDoesNotMutateStartingProfile()
    {
        var original=BuiltInPresets.Create()[0].Profile;var c=Candidate with {Quality=new(false,0,0)};
        var profile=WirelessBenchmark.Profile(original,c,1664,90);StockProfiles.Validate(profile);
        Assert.Equal(400,original.TargetMbps);Assert.Equal(300,profile.TargetMbps);Assert.False(profile.QualityEnabled);Assert.Equal(-20,original.GazeOffset);
    }
    [Fact] public void RecoveryOnlyRestoresOwnedSettings()
    {
        var v=BenchmarkSettingsLease.Keys.ToDictionary(k=>k,_=>JsonSerializer.SerializeToElement(100));
        var d=new BenchmarkLeaseData("config","session",new(),new(),new(),[],v,v);
        var live=new Dictionary<string,JsonElement>(v){["targetBandwidth"]=JsonSerializer.SerializeToElement(333)};
        Assert.DoesNotContain("targetBandwidth",BenchmarkSettingsLease.OwnedKeys(d,live));
    }
    [Fact] public void HeadsetReaderRejectsMissingOrWiredCounters()
    {
        Assert.ThrowsAny<Exception>(()=>BenchmarkHeadsetReader.Parse("{}"));
        Assert.Throws<IOException>(()=>BenchmarkHeadsetReader.Parse(JsonSerializer.Serialize(new BenchmarkHeadset(new(10,"id",100,100,0,0),"USB cable"))));
    }
    [Fact] public void UnattendedRenderingStillRequiresFreshDelivery()
    {
        var rows=Good().Select((s,i)=>s with {Scene=s.Scene with {Unattended=true,Tracked=false,InvalidPoses=i*90}}).ToArray();
        Assert.True(WirelessBenchmark.Evaluate(Candidate,rows,90,25).Passed);
        Assert.False(WirelessBenchmark.Evaluate(Candidate,Good(mbps:1),90,25).MeasurementValid);
    }
    [Fact] public void LogEvidenceDoesNotConvertSuppressionOrRepairsIntoFrameLoss()
    {
        var a=new StreamingHealth(Path.GetTempPath()).Current with {DriverLogAvailable=true,CompositorLogAvailable=true,StreamResetRequests=10};
        var b=a with {StreamResetRequests=13,SuppressedCompositorMessages=2700};
        var e=BenchmarkLogEvidence.Between(a,b);Assert.Equal(3,e.RecoveryRequests);Assert.True(e.Complete);
        Assert.False(BenchmarkLogEvidence.Between(a,b with {SkippedBytes=1}).Complete);
        var ra=new FrameReceiverDiagnostics(new()).Current with {Available=true,StreamPid=5,StreamStartTicks=10,LogGeneration="one",RetryEvents=2,RequestedRetryPackets=20};
        var re=BenchmarkReceiverLogEvidence.Between(ra,ra with {RetryEvents=3,RequestedRetryPackets=35});
        Assert.Equal(1,re.RetryRequests);Assert.Equal(15,re.RequestedPackets);
    }
    [Fact] public void ActivityIsBoundedAndObserverUsesOneCompositorConnection()
    {
        Assert.Contains("ExtendActivityTimeout(0, 60)",BenchmarkHeadsetReader.ActivityScript(60));
        Assert.Contains("ExtendActivityTimeout(0, 0)",BenchmarkHeadsetReader.ActivityScript(0));
        Assert.Throws<ArgumentOutOfRangeException>(()=>BenchmarkHeadsetReader.ActivityScript(600));
        Assert.Contains("FnTable:IVRCompositor_029",BenchmarkReceiverMonitor.Script);
        Assert.DoesNotContain("subprocess",BenchmarkReceiverMonitor.Script);
    }
}
