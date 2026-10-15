namespace SteamLinkVRSettings.Frame;

/// <summary>Independent region screens, followed by measured combinations; not an exhaustive grid.</summary>
internal static class BenchmarkQualitySearch
{
    public static readonly BenchmarkQuality Neutral=new(true,0,0);
    internal static int[] GazeValues(BenchmarkQuality? initial)=>Values([6,-6,-12,-20],initial is {Enabled:true}?initial.GazeOffset:0);
    internal static int[] PeripheralValues(BenchmarkQuality? initial)=>Values([8,-4,-8,-12],initial is {Enabled:true}?initial.PeripheralOffset:0);
    private static int[] Values(int[] values,int starting)=>values.Append(starting).Where(x=>x!=0).Distinct().ToArray();
    public static int MaximumTrials(BenchmarkQuality? initial)=>1+GazeValues(initial).Length+PeripheralValues(initial).Length+1+3;

    // A declared detail preference, not an optical image-quality measurement.
    // Actual bytes do not enter it; zero retains Valve's existing foveation map.
    internal static double DetailPreference(BenchmarkQuality? quality)
    {
        int gaze=quality is {Enabled:true}?quality.GazeOffset:0;
        int periphery=quality is {Enabled:true}?quality.PeripheralOffset:0;
        return .6*Math.Clamp(50-2.5*gaze,0,100)+.4*Math.Clamp(50-50d/12*periphery,0,100);
    }

    public static async Task<BenchmarkTrial> RunAsync(BenchmarkTrial lead,BenchmarkQuality? initial,BenchmarkOptions options,
        Func<BenchmarkCandidate,string,Task<BenchmarkTrial>> measure,CancellationToken token)
    {
        var observed=new Dictionary<BenchmarkQuality,BenchmarkTrial>();
        var basis=lead.Candidate with {FreshSession=false};
        async Task<BenchmarkTrial> M(BenchmarkQuality quality,string stage)
        {
            token.ThrowIfCancellationRequested();
            if(observed.TryGetValue(quality,out var existing))return existing;
            var trial=await measure(basis with {Quality=quality},stage);observed.Add(quality,trial);return trial;
        }
        await M(Neutral,"Quality baseline 0 / 0");
        foreach(int gaze in GazeValues(initial))await M(new(true,gaze,0),"Gaze comparison (periphery 0)");
        foreach(int periphery in PeripheralValues(initial))await M(new(true,0,periphery),"Periphery comparison (gaze 0)");
        if(initial is {Enabled:true})await M(initial,"Starting quality comparison");

        // Combine independently useful values, then check alternatives that relax
        // one region. An unmeasured combination can never become a recommendation.
        BenchmarkTrial[] Axis(bool gaze)=>observed.Values.Where(t=>t.MeasurementValid && !BenchmarkExplorer.Serious(t,options) &&
            (gaze?t.Candidate.Quality!.PeripheralOffset==0:t.Candidate.Quality!.GazeOffset==0))
            .OrderByDescending(t=>BenchmarkExplorer.Score(t)).Take(3).ToArray();
        var gazeOptions=Axis(true);var peripheryOptions=Axis(false);
        var pairs=(from g in gazeOptions from p in peripheryOptions
            select new BenchmarkQuality(true,g.Candidate.Quality!.GazeOffset,p.Candidate.Quality!.PeripheralOffset))
            .Distinct().Where(q=>!observed.ContainsKey(q)).ToList();
        if(gazeOptions.Length>0 && peripheryOptions.Length>0)
        {
            var first=new BenchmarkQuality(true,gazeOptions[0].Candidate.Quality!.GazeOffset,peripheryOptions[0].Candidate.Quality!.PeripheralOffset);
            pairs=pairs.OrderBy(q=>q==first?0:1).ThenByDescending(DetailPreference).ToList();
        }
        foreach(var pair in pairs.Take(3))await M(pair,"Combined gaze / periphery check");
        var baseline=lead.Candidate.Quality is {} q && observed.TryGetValue(q,out var refreshed)?refreshed:lead;
        return observed.Values.Aggregate(baseline,(best,next)=>BenchmarkExplorer.Prefer(next,best,options)?next:best);
    }
}

/// <summary>Whole-frame encoder metadata from reads bracketing the measurement window.</summary>
public sealed record BenchmarkQuantizer(double MeanQp,ulong Frames,double Seconds)
{
    internal static BenchmarkQuantizer? Between(EncoderSnapshot before,EncoderSnapshot after)
    {
        if(string.IsNullOrEmpty(before.Generation) || before.Generation!=after.Generation ||
            before.Codec!=after.Codec || before.BitDepth!=after.BitDepth || before.Enabled!=after.Enabled ||
            before.CenterOffset!=after.CenterOffset || before.PeripheralOffset!=after.PeripheralOffset ||
            before.EncodingPolicy?.Requested!=after.EncodingPolicy?.Requested ||
            before.EncodingPolicy?.Width!=after.EncodingPolicy?.Width || before.EncodingPolicy?.Height!=after.EncodingPolicy?.Height ||
            before.AdvancedEncoding?.Requested!=after.AdvancedEncoding?.Requested ||
            after.QuantizerSamples<before.QuantizerSamples || after.QuantizerSum<before.QuantizerSum ||
            after.EncodedFrames<before.EncodedFrames || after.EncodeErrors!=before.EncodeErrors || after.LockErrors!=before.LockErrors ||
            after.RejectedApiCalls!=before.RejectedApiCalls)
            return null;
        ulong samples=after.QuantizerSamples-before.QuantizerSamples,frames=after.EncodedFrames-before.EncodedFrames;
        double seconds=after.TimestampSeconds-before.TimestampSeconds;
        if(samples<24 || frames==0 || samples>frames || samples<frames*.9 || !double.IsFinite(seconds) || seconds<=0)return null;
        double mean=(after.QuantizerSum-before.QuantizerSum)/(double)samples;
        return double.IsFinite(mean) && mean is >=0 and <=63?new(mean,samples,seconds):null;
    }
    public string Summary=>$"Mean frame QP {MeanQp:0.0} ({Frames} frames). Whole-frame compression metadata; not a regional or perceptual quality score.";
}
