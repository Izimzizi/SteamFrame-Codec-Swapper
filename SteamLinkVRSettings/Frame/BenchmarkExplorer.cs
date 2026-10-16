namespace SteamLinkVRSettings.Frame;

/// <summary>A bounded mode/pass screen followed by adaptive bitrate probes and three exact confirmations.</summary>
internal static class BenchmarkExplorer
{
    internal static (int Width,int Fps)[] Modes(BenchmarkOptions options,int width,int fps)
    {
        if(width is <512 or >4096 || fps is not (90 or 120))throw new ArgumentException("Unsupported starting display mode.");
        int[] widths=options.TestResolutions ? new[]{1280,1664,2048,width}.Distinct().Order().ToArray() : [width];
        int[] rates=options.TestRefreshRates ? new[]{fps,fps==90?120:90} : [fps];
        return rates.SelectMany(hz=>widths.Select(w=>(w,hz))).ToArray();
    }
    internal static bool Serious(BenchmarkTrial t,BenchmarkOptions o) => t.Assessment is {} a &&
        (a.AffectedPercent>o.IssuePercent || a.WorstWindowAffectedMs>=o.IssueMilliseconds ||
         a.LongestSlowDeliverySeconds>=3 || a.ObservedNoDeliverySeconds>=1 ||
         t.EncodedFps<t.Candidate.Fps*.85 || t.ReceiverFps<t.Candidate.Fps*.85 ||
         // SteamVR lowered the stream below the requested rate: the link did not carry this candidate.
         t.LinkLimited);

    // Separate resolution/refresh preferences from coded bytes: extra pixels or
    // frames naturally consume bandwidth and are not themselves better encoding.
    internal static double Score(BenchmarkTrial t,string preference="Balanced")
    {
        double smooth=t.Assessment?.SmoothnessScore ?? 0;
        double detail=Math.Clamp(Math.Log2(Math.Max(512,t.Candidate.Width)/1024d),0,1.5)/1.5*100;
        double motion=t.Candidate.Fps==120?100:60;
        double quality=BenchmarkQualitySearch.DetailPreference(t.Candidate.Quality);
        return preference switch
        {
            "Higher detail"=>.45*smooth+.30*detail+.05*motion+.20*quality,
            "Higher refresh"=>.45*smooth+.15*detail+.25*motion+.15*quality,
            _=>.55*smooth+.25*detail+.05*motion+.15*quality
        };
    }
    internal static BenchmarkTrial[] Leaders(IEnumerable<BenchmarkTrial> source,BenchmarkOptions options)
    {
        var valid=source.Where(t=>t.MeasurementValid).GroupBy(t=>t.Candidate with {FreshSession=false})
            .Select(g=>g.OrderByDescending(t=>Serious(t,options)).ThenBy(t=>Score(t)).First())
            .GroupBy(t=>t.Candidate with {FreshSession=false,TargetMbps=0})
            .Select(g=>g.OrderBy(t=>t.Candidate.TargetMbps).Aggregate((best,next)=>Prefer(next,best,options)?next:best)).ToArray();
        var picked=new List<BenchmarkTrial>();
        foreach(var preference in new[]{"Balanced","Higher detail","Higher refresh"})
        {
            var candidate=valid.Where(t=>!picked.Any(p=>p.Candidate.Width==t.Candidate.Width && p.Candidate.Fps==t.Candidate.Fps))
                .OrderBy(t=>Serious(t,options))
                .ThenByDescending(t=>preference=="Higher detail"?t.Candidate.Width:preference=="Higher refresh"?t.Candidate.Fps:0)
                .ThenByDescending(t=>Score(t,preference)).FirstOrDefault();
            if(candidate is not null)picked.Add(candidate);
        }
        return picked.ToArray();
    }
    internal static bool Prefer(BenchmarkTrial next,BenchmarkTrial current,BenchmarkOptions options)
    {
        if(!next.MeasurementValid)return false;
        if(!current.MeasurementValid)return true;
        if(Serious(next,options)!=Serious(current,options))return !Serious(next,options);
        double advantage=Score(next)-Score(current);
        bool samePicture=(next.Candidate with {TargetMbps=0,FreshSession=false})==
            (current.Candidate with {TargetMbps=0,FreshSession=false});
        if(!samePicture)return advantage>0;
        // More bytes alone are not evidence of better quality. Compare whole-frame
        // QP only with the same codec (held by the runner), geometry, pass and map.
        double? qpGain=next.Quantizer is {} n && current.Quantizer is {} c?c.MeanQp-n.MeanQp:null;
        if(next.Candidate.TargetMbps>current.Candidate.TargetMbps)
            return advantage>5 || qpGain>=1 && advantage>=-3;
        if(next.Candidate.TargetMbps<current.Candidate.TargetMbps)
            return advantage>0 || advantage>=-2 && qpGain>=-1;
        return advantage>0;
    }
    public static BenchmarkResult Recommend(IReadOnlyList<BenchmarkTrial> trials,BenchmarkOptions options,bool asyncSend,
        string? interruption=null,int initialWidth=1664,int initialFps=90)
    {
        // A longer confirmation supersedes screens for that mode. It must not
        // erase useful evidence for other modes that never reached confirmation.
        var source=trials.Where(t=>t.MeasurementValid).GroupBy(t=>(t.Candidate.Width,t.Candidate.Fps))
            .SelectMany(g=>g.Any(t=>t.Stage=="Confirmation")?g.Where(t=>t.Stage=="Confirmation"):g).ToArray();
        var leaders=Leaders(source,options);
        var recommendations=leaders.Select((t,i)=>new BenchmarkRecommendation(i==0?"Balanced":$"Alternative {i+1}",t.Candidate,t,
            (t.Stage=="Confirmation"?"Confirmed":"Screened only; limited confidence")+" · "+
            (Serious(t,options)?"Above your serious-issue threshold; best available comparison, reduce load before play.":"Below your serious-issue threshold in this test.")+" "+t.Assessment?.Summary+(t.LinkNote is {} linkNote?" "+linkNote:""))).ToArray();
        // Always offer concrete settings, even when the hardware cannot be read.
        // Never manufacture observations or label this untested option stable.
        if(recommendations.Length==0)recommendations=[new("Unmeasured starting option",
            new(Math.Min(100,options.MaximumMbps),EncoderMultiPass.SinglePass,asyncSend,new(true,-4,0))
                {Width=options.TestResolutions?1280:initialWidth,Fps=options.TestRefreshRates?90:initialFps},null,
            "No valid measurement completed. These explicit low-load settings are untested on this setup; reconnect and rerun to compare modes.")];
        if(recommendations.Length>1)
        {
            int detail=Array.FindIndex(recommendations,1,r=>r.Candidate.Width>recommendations[0].Candidate.Width && r.Candidate.Width==recommendations.Max(x=>x.Candidate.Width));
            if(detail>0)recommendations[detail]=recommendations[detail] with {Name="Higher detail"};
            int motion=Array.FindIndex(recommendations,1,r=>r.Candidate.Fps>recommendations[0].Candidate.Fps && r.Name!="Higher detail");
            if(motion>0)recommendations[motion]=recommendations[motion] with {Name="Higher refresh"};
            for(int i=1;i<recommendations.Length;i++)
                if(recommendations[i].Name.StartsWith("Alternative ",StringComparison.Ordinal))
                    recommendations[i]=recommendations[i] with {Name=recommendations[i].Candidate.Width<recommendations[0].Candidate.Width
                        ? "Lower resolution" : recommendations[i].Candidate.Fps<recommendations[0].Candidate.Fps ? "Lower refresh" : recommendations[i].Name};
        }
        return new(recommendations[0].Trial,trials,
            (interruption is null?"Comparison complete.":"Comparison stopped: "+interruption)+
            " Choose a recommended option below. Starting settings are restored unless recovery is pending. Smoothness measures delivery hitches, not end-to-end latency or visual quality. Short tests do not guarantee every game.")
            {Recommendations=recommendations};
    }
    public static async Task<BenchmarkResult> RunAsync(BenchmarkOptions options,int ceiling,bool sending,
        Func<BenchmarkCandidate,int,CancellationToken,Task<BenchmarkTrial>> measure,Action<string> progress,CancellationToken token,
        Action<BenchmarkTrial>? completed,BenchmarkQuality? quality,int width,int fps,BenchmarkProgressTracker? tracker=null)
    {
        options.Validate();var modes=Modes(options,width,fps);int maximum=Math.Min(ceiling,options.MaximumMbps);
        if(maximum<100 || quality is {IsValid:false})throw new ArgumentException("Unsupported ceiling or quality offsets.");
        var trials=new List<BenchmarkTrial>();
        int qualityTrials=options.AllowQualityReduction?BenchmarkQualitySearch.MaximumTrials(quality):0;
        tracker?.Plan(modes.Length*(options.FullMultipass?3:2),Math.Min(3,modes.Length),qualityTrials,options);
        async Task<BenchmarkTrial> M(BenchmarkCandidate c,int seconds,string stage)
        {
            tracker?.Screen(c,seconds);
            token.ThrowIfCancellationRequested();progress(stage+": "+c.Label);
            var t=(await measure(c,seconds,token)) with {Stage=stage};trials.Add(t);tracker?.TrialFinished(t.Seconds);completed?.Invoke(t);return t;
        }
        var passes=options.FullMultipass ? new[]{EncoderMultiPass.SinglePass,EncoderMultiPass.QuarterResolution,EncoderMultiPass.FullResolution}
            : [EncoderMultiPass.SinglePass,EncoderMultiPass.QuarterResolution];
        // Multipass changes require a fresh encoder. Group by pass and reverse
        // the geometry sweep to avoid a restart for every individual mode.
        for(int passIndex=0;passIndex<passes.Length;passIndex++)
        {
            foreach(var mode in passIndex%2==0?modes:modes.Reverse())
            {
                int target=Math.Clamp((int)(150*Math.Pow(mode.Width/1664d,2)*mode.Fps/90/5)*5,50,maximum);
                await M(new(target,passes[passIndex],sending,options.AllowQualityReduction?BenchmarkQualitySearch.Neutral:quality)
                    {Width=mode.Width,Fps=mode.Fps},options.TrialSeconds,"Mode/pass screen");
            }
        }
        return await RefineAsync(trials,options,maximum,sending,measure,progress,token,completed,quality,width,fps,tracker);
    }
    internal static async Task<BenchmarkResult> RefineAsync(List<BenchmarkTrial> trials,BenchmarkOptions options,int maximum,bool sending,
        Func<BenchmarkCandidate,int,CancellationToken,Task<BenchmarkTrial>> measure,Action<string> progress,CancellationToken token,
        Action<BenchmarkTrial>? completed,BenchmarkQuality? quality,int width,int fps,BenchmarkProgressTracker? tracker=null)
    {
        async Task<BenchmarkTrial> M(BenchmarkCandidate c,int seconds,string stage)
        {
            tracker?.Refinement(c,seconds,stage);
            token.ThrowIfCancellationRequested();progress(stage+": "+c.Label);
            var t=(await measure(c,seconds,token)) with {Stage=stage};trials.Add(t);tracker?.TrialFinished(t.Seconds);completed?.Invoke(t);return t;
        }
        // Only refine three promising, distinct modes instead of exhausting
        // every bitrate/quality combination at every geometry.
        var leaders=Leaders(trials,options);
        int choiceIndex=0;
        foreach(var lead in leaders.OrderBy(t=>t.Candidate.Fps).ThenBy(t=>t.Candidate.Width))
        {
            int qualityTrials=options.AllowQualityReduction?BenchmarkQualitySearch.MaximumTrials(quality):0;
            tracker?.Choice(choiceIndex++,leaders.Length,options,qualityTrials);
            var best=lead;
            if(options.AllowQualityReduction)best=await BenchmarkQualitySearch.RunAsync(lead,quality,options,
                (candidate,stage)=>M(candidate,options.TrialSeconds,stage),token);
            for(int step=0;step<2;step++)
            {
                int next=Serious(best,options)?Math.Max(25,best.Candidate.TargetMbps/2/5*5):Math.Min(maximum,best.Candidate.TargetMbps+125);
                if(next==best.Candidate.TargetMbps)break;
                var trial=await M(best.Candidate with {TargetMbps=next},options.TrialSeconds,"Bitrate search");
                if(!trial.MeasurementValid)break;
                if(Prefer(trial,best,options))best=trial;
                else break;
                if(Serious(trial,options) && next>lead.Candidate.TargetMbps)break;
            }
            BenchmarkCandidate WithHeadroom(BenchmarkCandidate c)=>c with
                {TargetMbps=Math.Max(25,c.TargetMbps*(100-options.HeadroomPercent)/100/5*5),FreshSession=true};
            var final=WithHeadroom(best.Candidate);
            var confirmation=await M(final,options.ValidationSeconds,"Confirmation");
            if(!confirmation.MeasurementValid || Serious(confirmation,options))
            {
                // One bounded alternative: do not leave a bad confirmation as
                // the only option for a mode when another measured pass/map worked.
                var alternative=trials.Where(t=>t.MeasurementValid && !Serious(t,options) &&
                    t.Candidate.Width==final.Width && t.Candidate.Fps==final.Fps &&
                    (t.Candidate.MultiPass!=final.MultiPass || t.Candidate.Quality!=final.Quality))
                    .OrderByDescending(t=>Score(t)).FirstOrDefault();
                if(alternative is not null)await M(WithHeadroom(alternative.Candidate),options.ValidationSeconds,"Confirmation");
            }
            tracker?.ChoiceFinished();
        }
        tracker?.SearchFinished();
        return Recommend(trials,options,sending,initialWidth:width,initialFps:fps);
    }
}
