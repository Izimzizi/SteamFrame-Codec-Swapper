namespace SteamLinkVRSettings.Frame;

/// <summary>One-second counter windows cannot identify unique missed optical frames or exact stutter durations.</summary>
public sealed record BenchmarkAssessment(bool MeasurementValid, bool MajorIssues, long AffectedFrameEstimate,
    double AffectedPercent, int HitchWindows, int BurstWindows, double BurstsPerMinute,
    double WorstWindowAffectedMs, double LongestSlowDeliverySeconds, double ObservedNoDeliverySeconds,
    double SmoothnessScore, double UtilityScore, string Description)
{
    public string Summary => !MeasurementValid ? Description :
        $"{Description} · {AffectedPercent:0.###}% affected-frame estimate · {HitchWindows} affected sampling windows · " +
        $"{BurstWindows} burst windows · smoothness {SmoothnessScore:0.0}/100" +
        (LongestSlowDeliverySeconds > 0 ? $" · longest delivery dip {LongestSlowDeliverySeconds:0.0}s" : "");
}

internal static class BenchmarkScoring
{
    public static BenchmarkTrial Evaluate(BenchmarkCandidate candidate, IReadOnlyList<BenchmarkSample> samples, int fps, int requiredSeconds, bool completeWindow = true)
    {
        double duration = samples.Count > 1 ? samples[^1].Seconds - samples[0].Seconds : 0;
        bool timed = double.IsFinite(duration) && duration > 0;
        double mbps = timed && samples[^1].Bytes >= samples[0].Bytes ? (samples[^1].Bytes - samples[0].Bytes) * 8d / duration / 1e6 : 0;
        double encoded = timed && samples[^1].Frames >= samples[0].Frames ? (samples[^1].Frames - samples[0].Frames) / duration : 0;
        BenchmarkTrial Invalid(string why) => new(candidate, false, why, timed ? duration : 0, mbps, encoded, 0, 0, samples)
        { Assessment = new(false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, why) };
        if (fps is not (90 or 120) || samples.Count < 3) return Invalid("Not enough measurement data.");
        if (!timed || duration < requiredSeconds || duration > requiredSeconds + 10) return Invalid("The measurement window was incomplete or stalled.");
        var first = samples[0]; var last = samples[^1];
        foreach (var s in samples)
        {
            if (!double.IsFinite(s.Seconds) || !double.IsFinite(s.Scene.Seconds) || !double.IsFinite(s.Receiver.Seconds) ||
                !double.IsFinite(s.GpuP99Ms) || s.GpuP99Ms <= 0 || string.IsNullOrEmpty(s.Identity) ||
                s.PcDrops<0 || s.PcLate<0 || s.PcTimeouts<0 || s.PcTimeoutPresentations<0 || s.Receiver.Drops<0 || s.Receiver.Timeouts<0 ||
                string.IsNullOrEmpty(s.Receiver.Identity) || s.Identity != first.Identity || s.Receiver.Identity != first.Receiver.Identity ||
                s.WirelessPath != first.WirelessPath || s.WirelessPath is not ("Dedicated wireless" or "Local network"))
                return Invalid("The stream, receiver or wireless measurement changed.");
            if (!s.ConfigurationAccepted) return Invalid("The requested encoder settings were not active.");
            if (s.Scene.Fps != fps) return Invalid("The headset refresh rate changed.");
            if (s.Scene.Unattended != first.Scene.Unattended || !s.Scene.Unattended && !s.Scene.Tracked)
                return Invalid("The tracking mode changed or head tracking is unavailable.");
        }
        long affected = 0; int hitches = 0, bursts = 0; double worst = 0, slowRun = 0, longestSlow = 0, noDelivery = 0, longestNoDelivery = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            var a = samples[i - 1]; var b = samples[i]; double dt = b.Seconds - a.Seconds;
            double sceneDt = b.Scene.Seconds - a.Scene.Seconds, remoteDt = b.Receiver.Seconds - a.Receiver.Seconds;
            if (dt <= 0 || dt > 3 || sceneDt <= 0 || remoteDt < 0 || b.Frames < a.Frames || b.Bytes < a.Bytes ||
                b.Receiver.Submits < a.Receiver.Submits || b.Receiver.Presents < a.Receiver.Presents || b.Scene.Frames < a.Scene.Frames)
                return Invalid("Video or measurement counters reset or became stale.");
            if (b.Errors != a.Errors) return Invalid("The encoder reported an error or its counters reset.");
            if (b.HistoryGaps != a.HistoryGaps) return Invalid("The PC presentation history had an unobserved gap.");
            if (b.PcDrops < a.PcDrops || b.PcLate < a.PcLate || b.PcTimeouts < a.PcTimeouts ||
                b.Receiver.Drops < a.Receiver.Drops || b.Receiver.Timeouts < a.Receiver.Timeouts || b.Scene.LongIntervals < a.Scene.LongIntervals)
                return Invalid("A presentation counter reset.");
            if (a.PcTimeoutPresentations.HasValue != b.PcTimeoutPresentations.HasValue || b.PcTimeoutPresentations < a.PcTimeoutPresentations)
                return Invalid("The PC timeout-presentation counter reset or changed availability.");
            if (!b.Scene.Unattended && b.Scene.InvalidPoses != a.Scene.InvalidPoses) return Invalid("Head tracking was lost.");
            if (b.Scene.SubmitErrors != a.Scene.SubmitErrors) return Invalid("The test scene could not submit a frame.");
            double sourceFps = (b.Scene.Frames - a.Scene.Frames) / sceneDt;
            double encodeFps = (b.Frames - a.Frames) / dt;
            double remoteFps = remoteDt > 0 ? (b.Receiver.Submits - a.Receiver.Submits) / remoteDt : fps;
            long Deficit(double actualFps) => actualFps < fps * .97 ? (long)Math.Ceiling((fps - actualFps) * dt) : 0;
            // These counters can describe the same missed image. Take the largest
            // observation in each window rather than adding PC + receiver losses.
            long hit = new[] { b.PcDrops-a.PcDrops, b.PcLate-a.PcLate,
                (b.PcTimeoutPresentations ?? b.PcTimeouts)-(a.PcTimeoutPresentations ?? a.PcTimeouts),
                b.Receiver.Drops-a.Receiver.Drops, b.Receiver.Timeouts-a.Receiver.Timeouts,
                Deficit(sourceFps), Deficit(encodeFps), Deficit(remoteFps) }.Max();
            affected += hit;
            if (hit > 0) hitches++;
            if (hit >= 3) bursts++;
            worst = Math.Max(worst, hit * 1000d / fps);
            slowRun = Math.Min(sourceFps, Math.Min(encodeFps, remoteFps)) < fps * .90 ? slowRun + dt : 0;
            longestSlow = Math.Max(longestSlow, slowRun);
            noDelivery = b.Frames == a.Frames || remoteDt > 0 && b.Receiver.Submits == a.Receiver.Submits ? noDelivery + dt : 0;
            longestNoDelivery = Math.Max(longestNoDelivery, noDelivery);
        }
        double remoteSeconds = last.Receiver.Seconds - first.Receiver.Seconds;
        if (remoteSeconds < requiredSeconds - 5 || remoteSeconds <= 0 || remoteSeconds > duration + 5)
            return Invalid("Fresh headset measurements did not cover the test.");
        double receiverFps = (last.Receiver.Submits-first.Receiver.Submits)/remoteSeconds;
        double sceneSeconds = last.Scene.Seconds-first.Scene.Seconds;
        double sourceMean = (last.Scene.Frames-first.Scene.Frames)/sceneSeconds;
        double gpu = samples.Max(s => s.GpuP99Ms), percent = Math.Min(100, affected*100d/(fps*duration));
        double burstRate = bursts*60d/duration;
        bool major = percent > 1 || worst >= 250 || longestSlow >= 3 || longestNoDelivery >= .5 ||
            encoded < fps*.95 || receiverFps < fps*.95 || sourceMean < fps*.95 || gpu > 1000d/fps*.80;
        // A short low-output interval can recover within the requested window.
        // Keep measuring it; only the completed window can establish insufficient workload.
        // Counter resets, lost configuration and other invalid evidence still stop immediately.
        if (completeWindow && mbps < Math.Min(25, candidate.TargetMbps*.25)) return Invalid("Too little picture data was encoded; the scene may be idle.");
        double smooth = Math.Clamp(100-30*percent-Math.Min(30,burstRate*2)-Math.Min(35,worst/10)-Math.Min(25,longestSlow*8),0,100);
        string description = major ? "Frequent hitches or delivery slowdowns observed" : affected == 0 ? "No measured delivery hitches" : "Usable with occasional delivery hitches";
        var trial=new BenchmarkTrial(candidate,!major,description,duration,mbps,encoded,receiverFps,gpu,samples)
        { Assessment = new(true,major,affected,percent,hitches,bursts,burstRate,worst,longestSlow,longestNoDelivery,smooth,0,description) };
        return trial with {Assessment=trial.Assessment with {UtilityScore=BenchmarkExplorer.Score(trial)}};
    }

    public static double Utility(BenchmarkTrial trial) => BenchmarkExplorer.Score(trial);
    public static IEnumerable<BenchmarkTrial> Rank(IEnumerable<BenchmarkTrial> trials) => trials.Where(t => t.MeasurementValid)
        .OrderBy(t=>BenchmarkExplorer.Serious(t,new()))
        .ThenByDescending(t => Math.Round(Utility(t),0))
        .ThenBy(t => t.Candidate.TargetMbps).ThenBy(t => t.Candidate.MultiPass);
}
