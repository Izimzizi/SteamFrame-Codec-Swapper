namespace SteamLinkVRSettings.Frame;

public enum SweepVerdict { Clean, Marginal, Failed }

/// <summary>What one sweep step observed after its settle time. Null means the source was unavailable.</summary>
public sealed record SweepSample(
    double Seconds, long ThrottleEvents, double ThrottleMs, long BadLinkEvents, long BitrateChanges,
    double? LowestDriverBitrateMbps, double? MaxLossRatio, double? MaxLatencyMs,
    long PcDroppedFrames, double? MeanVideoMbps, double? MeanFps, long ReceiverRetryEvents, long StreamResets);

public sealed record SweepStepResult(int TargetMbps, SweepSample Sample, SweepVerdict Verdict, string Reason)
{
    public double ThrottlesPerMinute => Sample.Seconds > 0 ? Sample.ThrottleEvents * 60.0 / Sample.Seconds : 0;
    public double HeldMsPerMinute => Sample.Seconds > 0 ? Sample.ThrottleMs * 60.0 / Sample.Seconds : 0;
}

public sealed record SweepConclusion(int? BestCleanMbps, int? HighestMarginalMbps, string Summary);

/// <summary>
/// Plans and judges a bitrate sweep: the app raises the manual target in steps while a stream runs,
/// measures each step, and reports the highest rate the link carried without stalls.
/// The page applies and restores targets; this class only decides.
/// </summary>
public static class LinkCapacitySweep
{
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan MeasureTime = TimeSpan.FromSeconds(24);
    public const int DefaultStepMbps = 50, MinimumStepSeconds = 10;
    public const double FailedLoss = 0.01, MarginalLoss = 0.002, MarginalLatencyMs = WirelessAdvisor.LatencyNoticeMs;

    /// <summary>Ascending targets from the start to the ceiling, always ending on the ceiling itself.</summary>
    public static IReadOnlyList<int> Plan(int startMbps, int ceilingMbps, int stepMbps = DefaultStepMbps)
    {
        if (stepMbps < 5) throw new ArgumentOutOfRangeException(nameof(stepMbps));
        startMbps = Math.Clamp(startMbps, 25, WirelessAdvisor.PatchedCeilingMbps);
        ceilingMbps = Math.Clamp(ceilingMbps, startMbps, WirelessAdvisor.PatchedCeilingMbps);
        var steps = new List<int>();
        for (var target = startMbps; target < ceilingMbps; target += stepMbps) steps.Add(target);
        if (steps.Count == 0 || steps[^1] != ceilingMbps) steps.Add(ceilingMbps);
        // Avoid a tiny last step such as 450 → 460.
        if (steps.Count >= 2 && steps[^1] - steps[^2] < stepMbps / 2) steps.RemoveAt(steps.Count - 2);
        return steps;
    }

    /// <summary>Held-frame limits scale with the refresh rate: 0.5% of frames is marginal, 2% is a failed step.</summary>
    public static SweepStepResult Evaluate(int targetMbps, SweepSample sample, int refreshHz = 90)
    {
        if (sample.Seconds < MinimumStepSeconds)
            return new(targetMbps, sample, SweepVerdict.Failed, "The step ended before enough time was measured.");
        var perMinute = sample.ThrottleEvents * 60.0 / sample.Seconds;
        var heldMsPerMinute = sample.ThrottleMs * 60.0 / sample.Seconds;
        var fps = sample.MeanFps is double measured && measured >= 60 ? (int)Math.Round(measured) : refreshHz;
        var limits = WirelessAdvisor.HeldFrameLimits(fps);
        var loweredBelowTarget = sample.BitrateChanges > 0 && sample.LowestDriverBitrateMbps is double lowest && lowest < targetMbps - 1;
        var reasons = new List<string>();
        var verdict = SweepVerdict.Clean;
        void Fail(string why) { verdict = SweepVerdict.Failed; reasons.Add(why); }
        void Marginal(string why) { if (verdict == SweepVerdict.Clean) verdict = SweepVerdict.Marginal; reasons.Add(why); }
        if (loweredBelowTarget) Fail($"SteamVR lowered the stream to {sample.LowestDriverBitrateMbps:0} Mbps during the step.");
        if (perMinute >= limits.Strong || heldMsPerMinute >= WirelessAdvisor.StrongHeldMsPerMinute) Fail($"{perMinute:0} held frames per minute ({heldMsPerMinute:0} ms).");
        else if (perMinute >= limits.Mild || heldMsPerMinute >= WirelessAdvisor.MildHeldMsPerMinute) Marginal($"{perMinute:0} held frames per minute ({heldMsPerMinute:0} ms).");
        if (sample.MaxLossRatio is double loss)
        {
            if (loss >= FailedLoss) Fail($"{loss:P1} headset packet loss.");
            else if (loss >= MarginalLoss) Marginal($"{loss:P2} headset packet loss.");
        }
        if (sample.StreamResets > 0) Fail($"{sample.StreamResets} stream recovery request{(sample.StreamResets == 1 ? "" : "s")}.");
        if (sample.MaxLatencyMs is > MarginalLatencyMs) Marginal($"{sample.MaxLatencyMs:0} ms worst link latency.");
        if (sample.ReceiverRetryEvents >= 5) Marginal($"{sample.ReceiverRetryEvents} receiver retry requests.");
        if (verdict == SweepVerdict.Clean)
            reasons.Add(sample.MeanVideoMbps is double video ? $"Clean; encoded video averaged {video:0} Mbps." : "Clean; no stalls, loss or held frames.");
        if (sample.PcDroppedFrames > 0) reasons.Add($"{sample.PcDroppedFrames} PC presentation drops (not counted against the link).");
        return new(targetMbps, sample, verdict, string.Join(" ", reasons));
    }

    /// <summary>Stop after a failed step, or after two marginal steps in a row: higher targets will not get better.</summary>
    public static bool ShouldStop(IReadOnlyList<SweepStepResult> results)
        => results.Count > 0 && (results[^1].Verdict == SweepVerdict.Failed
            || results.Count >= 2 && results[^1].Verdict == SweepVerdict.Marginal && results[^2].Verdict == SweepVerdict.Marginal);

    public static SweepConclusion Conclude(IReadOnlyList<SweepStepResult> results)
    {
        if (results.Count == 0) return new(null, null, "No steps were measured.");
        int? best = null, marginal = null;
        foreach (var result in results)
        {
            if (result.Verdict == SweepVerdict.Clean) best = result.TargetMbps;
            else if (result.Verdict == SweepVerdict.Marginal) marginal = Math.Max(marginal ?? 0, result.TargetMbps);
        }
        // A clean step above a failed one is suspicious; trust the lowest failure.
        var firstFailure = results.FirstOrDefault(r => r.Verdict == SweepVerdict.Failed)?.TargetMbps;
        if (firstFailure is int failed && best is int b && b > failed) best = results.Where(r => r.Verdict == SweepVerdict.Clean && r.TargetMbps < failed).Select(r => (int?)r.TargetMbps).LastOrDefault();
        var summary = best is int clean
            ? $"Highest clean target: {clean} Mbps" + (marginal is int m && m > clean ? $"; {m} Mbps was marginal" : "") + (firstFailure is int f ? $"; {f} Mbps failed." : ".")
            : marginal is int only ? $"No step was fully clean; {only} Mbps was the best marginal result." : "Every step showed link trouble; check the radio signal and dongle placement before streaming at higher rates.";
        return new(best, marginal, summary);
    }
}
