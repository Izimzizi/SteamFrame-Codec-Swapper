namespace SteamLinkVRSettings.Frame;

public enum SweepVerdict { Clean, Marginal, Failed }

/// <summary>What one sweep step observed after its settle time. Null means the source was unavailable.</summary>
public sealed record SweepSample(
    double Seconds, long ThrottleEvents, double ThrottleMs, long BadLinkEvents, long BitrateChanges,
    double? LowestDriverBitrateMbps, double? MaxLossRatio, double? MaxLatencyMs,
    long PcDroppedFrames, double? MeanVideoMbps, double? MeanFps, long ReceiverRetryEvents, long StreamResets);

public sealed record SweepStepResult(int TargetMbps, SweepSample Sample, SweepVerdict Verdict, string Reason, int Passes = 1)
{
    public double ThrottlesPerMinute => Sample.Seconds > 0 ? Sample.ThrottleEvents * 60.0 / Sample.Seconds : 0;
    public double HeldMsPerMinute => Sample.Seconds > 0 ? Sample.ThrottleMs * 60.0 / Sample.Seconds : 0;
}

public sealed record SweepConclusion(int? BestCleanMbps, int? HighestMarginalMbps, string Summary, double? CarriedVideoMbps = null, bool Check = false, bool Confirmed = false)
{
    /// <summary>True when the encoder produced well under the clean target, so higher rates were only lightly tested.</summary>
    public bool LightlyTested => BestCleanMbps is int best &&
        (CarriedVideoMbps is not double carried || !double.IsFinite(carried) || carried < best * LinkCapacitySweep.FullTestShare);
    /// <summary>Shared by automatic apply, one-click apply and the advisor. Missing output is not a measured ceiling.</summary>
    public int? ApplicableCeilingMbps => !Check && Confirmed && !LightlyTested ? BestCleanMbps : null;
}

/// <summary>
/// Plans and judges a bitrate sweep: the app raises the manual target in steps while a stream runs,
/// measures each step, and reports the highest rate the link carried without stalls.
/// The page applies and restores targets; this class only decides.
/// </summary>
public static class LinkCapacitySweep
{
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan MeasureTime = TimeSpan.FromSeconds(24);
    /// <summary>
    /// A freshly started stream (or a scene switch) asks for stream resets for a while; the first step waits until
    /// none arrived for this long, up to the limit, so start-up noise is not scored as the link failing.
    /// </summary>
    public static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(10), QuietLimit = TimeSpan.FromSeconds(40);
    public const int DefaultStepMbps = 50, MinimumStepSeconds = 10;
    /// <summary>Extra passes a sweep may spend re-checking marginal steps, plus one confirmation of the winner.</summary>
    public const int MaxRechecks = 2, ExtraPasses = MaxRechecks + 1;
    /// <summary>A clean step counts as fully tested when encoded video averaged at least this share of its target.</summary>
    public const double FullTestShare = 0.85;
    public const double FailedLoss = 0.01, MarginalLoss = 0.002, MarginalLatencyMs = WirelessAdvisor.LatencyNoticeMs;
    /// <summary>
    /// Receiver retry requests are how lost packets get recovered; the loss ratio and held frames are the outcome.
    /// A moving scene at full rate logs a few per step on a healthy link, so only a flood counts against a step.
    /// </summary>
    public const double RetryFloodPerMinute = 60;

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
        var retriesPerMinute = sample.ReceiverRetryEvents * 60.0 / sample.Seconds;
        if (retriesPerMinute >= RetryFloodPerMinute) Marginal($"{sample.ReceiverRetryEvents} receiver retry requests ({retriesPerMinute:0} per minute).");
        if (verdict == SweepVerdict.Clean)
            reasons.Add(sample.MeanVideoMbps is double video ? $"Clean; encoded video averaged {video:0} Mbps." : "Clean; no stalls, loss or held frames.");
        if (retriesPerMinute > 0 && retriesPerMinute < RetryFloodPerMinute) reasons.Add($"{sample.ReceiverRetryEvents} receiver retry request{(sample.ReceiverRetryEvents == 1 ? "" : "s")}, recovered.");
        if (sample.PcDroppedFrames > 0) reasons.Add($"{sample.PcDroppedFrames} PC presentation drops (not counted against the link).");
        return new(targetMbps, sample, verdict, string.Join(" ", reasons));
    }

    /// <summary>A marginal or failed first pass is measured again: one burst of interference should not end the sweep.</summary>
    public static bool NeedsRecheck(SweepStepResult result, int rechecksUsed)
        => result.Verdict != SweepVerdict.Clean && result.Passes == 1 && rechecksUsed < MaxRechecks;

    /// <summary>
    /// Combines a marginal or failed first pass with its re-check. A clean second pass clears a marginal step,
    /// because a single bad window is more likely interference than a limit, and softens a failed step to
    /// marginal so the sweep goes on without recommending that rate; anything else keeps the worse verdict.
    /// </summary>
    public static SweepStepResult Recheck(SweepStepResult first, SweepStepResult second)
    {
        if (first.TargetMbps != second.TargetMbps) throw new ArgumentException("Re-check a step at its own target.", nameof(second));
        if (second.Verdict == SweepVerdict.Clean && first.Verdict == SweepVerdict.Failed)
            return second with { Verdict = SweepVerdict.Marginal, Reason = $"Clean on the re-check, but the first pass failed ({Trim(first.Reason)}); not recommended on its own.", Passes = first.Passes + 1 };
        if (second.Verdict == SweepVerdict.Clean)
            return second with { Reason = $"Clean on the re-check (the first pass saw: {Trim(first.Reason)}).", Passes = first.Passes + 1 };
        var verdict = (SweepVerdict)Math.Max((int)first.Verdict, (int)second.Verdict);
        var sample = second.Verdict >= first.Verdict ? second.Sample : first.Sample;
        var reason = second.Verdict >= first.Verdict
            ? $"{Trim(second.Reason)} on the re-check (the first pass saw: {Trim(first.Reason)})."
            : $"{Trim(first.Reason)} on the first pass (the re-check saw: {Trim(second.Reason)}).";
        return new(first.TargetMbps, sample, verdict, reason, first.Passes + 1);
    }

    /// <summary>
    /// Confirms the step the sweep is about to recommend with one more pass at the same target. A clean
    /// confirmation keeps the step; a marginal or failed one downgrades it so the ceiling moves down a step.
    /// </summary>
    public static SweepStepResult Confirm(SweepStepResult best, SweepStepResult confirmation)
    {
        if (best.TargetMbps != confirmation.TargetMbps) throw new ArgumentException("Confirm a step at its own target.", nameof(confirmation));
        if (confirmation.Verdict == SweepVerdict.Clean)
            return confirmation with { Reason = $"Confirmed clean on a second pass ({Trim(confirmation.Reason)}).", Passes = best.Passes + 1 };
        return confirmation with { Reason = $"{Trim(confirmation.Reason)} on the confirmation pass after a clean first pass.", Passes = best.Passes + 1 };
    }

    private static string Trim(string reason) => reason.TrimEnd('.', ' ');

    public const int HistoryLimit = 20;
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(7);

    /// <summary>Appends a finished sweep and drops the oldest beyond the limit.</summary>
    public static void Record(List<SweepRecord> history, SweepRecord record)
    {
        history.Add(record);
        if (history.Count > HistoryLimit) history.RemoveRange(0, history.Count - HistoryLimit);
    }

    /// <summary>
    /// For a link whose clean ceilings differed by a step or more within the week, the highest of them: the rate
    /// automatic mode should start from so SteamVR begins high and backs off when the link dips. Null when the
    /// sweeps agree (a fixed target serves better) or there are fewer than two clean ones to compare.
    /// </summary>
    public static int? AutomaticStart(IReadOnlyList<SweepRecord> history, DateTimeOffset now)
    {
        var clean = history.Where(r => r.IsFullyTested && now - r.At <= HistoryWindow && r.At - now <= TimeSpan.FromMinutes(5)).Select(r => r.CeilingMbps).ToList();
        if (clean.Count < 2) return null;
        int low = clean.Min(), high = clean.Max();
        return high - low >= DefaultStepMbps ? high : null;
    }

    /// <summary>
    /// One line on how the last week's sweeps compare, or null until there are two to compare. A spread of a
    /// step or more means the link itself varies, which is worth more to the user than any single number.
    /// </summary>
    public static string? DescribeHistory(IReadOnlyList<SweepRecord> history, DateTimeOffset now)
    {
        var window = history.Where(r => now - r.At <= HistoryWindow && r.At - now <= TimeSpan.FromMinutes(5)).OrderBy(r => r.At).ToList();
        var recent = window.Where(r => r.IsFullyTested).ToList();
        var noClean = window.Count(r => r.NoCleanStep);
        var unconfirmed = window.Count(r => !r.NoCleanStep && !r.Confirmed);
        var light = window.Count - recent.Count - noClean - unconfirmed;
        var noCleanText = (noClean == 0 ? "" : noClean == 1 ? " One sweep found no clean step at all." : $" {noClean} sweeps found no clean step at all.")
            + (light == 0 ? "" : light == 1 ? " One result without enough encoded video is not counted." : $" {light} results without enough encoded video are not counted.")
            + (unconfirmed == 0 ? "" : unconfirmed == 1 ? " One unconfirmed result is not counted." : $" {unconfirmed} unconfirmed results are not counted.");
        if (recent.Count < 2) return noClean > 0 && window.Count >= 2 ? $"The last {window.Count} sweeps disagreed." + noCleanText + " Sweep again before trusting any single result." : null;
        int low = recent.Min(r => r.CeilingMbps), high = recent.Max(r => r.CeilingMbps);
        var runs = recent.Count == 2 ? "two sweeps" : $"{recent.Count} sweeps";
        var listing = string.Join(", ", recent.TakeLast(4).Select(r => $"{r.CeilingMbps} Mbps at {r.At.ToLocalTime():MMM d HH:mm}"));
        if (high - low < DefaultStepMbps) return $"The last {runs} agreed within a step: {listing}." + noCleanText;
        return $"The last {runs} ranged from {low} to {high} Mbps ({listing}).{noCleanText} This link changes from hour to hour: settle on {low} Mbps for a quiet stream, or use automatic bitrate starting at {high} Mbps in Stream settings so SteamVR backs off when the link dips.";
    }

    private static readonly System.Text.RegularExpressions.Regex MeasuredPresetPattern =
        new(@"^Measured ceiling [0-9]{2,3} Mbps [0-9]{2}-[0-9]{2} [0-9]{2}-[0-9]{2}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The preset the sweep saves for its ceiling; only the latest one is kept.</summary>
    public static string MeasuredPresetName(int mbps, DateTime at) => $"Measured ceiling {mbps} Mbps {at:MM-dd HH-mm}";

    /// <summary>True for presets this app named itself, so renamed or user-made presets are never replaced.</summary>
    public static bool IsMeasuredPresetName(string name) => MeasuredPresetPattern.IsMatch(name);

    private static string Join(IReadOnlyList<int> values)
        => values.Count switch { 0 => "", 1 => $"{values[0]}", _ => string.Join(", ", values.Take(values.Count - 1)) + " and " + values[^1] };

    /// <summary>Stop after a failed step, or after two marginal steps in a row: higher targets will not get better.</summary>
    public static bool ShouldStop(IReadOnlyList<SweepStepResult> results)
        => results.Count > 0 && (results[^1].Verdict == SweepVerdict.Failed
            || results.Count >= 2 && results[^1].Verdict == SweepVerdict.Marginal && results[^2].Verdict == SweepVerdict.Marginal);

    /// <summary>A single-step check of the current rate: the verdict for today, never a ceiling to save.</summary>
    public static SweepConclusion ConcludeCheck(SweepStepResult result)
    {
        var verdict = result.Verdict switch { SweepVerdict.Clean => "clean", SweepVerdict.Marginal => "marginal", _ => "failing" };
        var summary = $"Checked {result.TargetMbps} Mbps just now: {verdict}. {result.Reason}";
        return new(result.Verdict == SweepVerdict.Clean ? result.TargetMbps : null, result.Verdict == SweepVerdict.Marginal ? result.TargetMbps : null, summary, result.Sample.MeanVideoMbps, Check: true);
    }

    public static SweepConclusion Conclude(IReadOnlyList<SweepStepResult> results)
    {
        if (results.Count == 0) return new(null, null, "No steps were measured.");
        int? best = null, marginal = null;
        var marginals = new List<int>();
        foreach (var result in results)
        {
            if (result.Verdict == SweepVerdict.Clean) best = result.TargetMbps;
            else if (result.Verdict == SweepVerdict.Marginal) { marginal = Math.Max(marginal ?? 0, result.TargetMbps); marginals.Add(result.TargetMbps); }
        }
        // A clean step above a failed one is suspicious; trust the lowest failure.
        var firstFailure = results.FirstOrDefault(r => r.Verdict == SweepVerdict.Failed)?.TargetMbps;
        if (firstFailure is int failed && best is int b && b > failed) best = results.Where(r => r.Verdict == SweepVerdict.Clean && r.TargetMbps < failed).Select(r => (int?)r.TargetMbps).LastOrDefault();
        // Every marginal step is named, below the winner too: a rate that failed once and passed its re-check belongs in the record.
        var listed = marginals.Distinct().OrderBy(m => m).ToList();
        var marginalText = listed.Count == 0 ? "" : listed.Count == 1 ? $"; {listed[0]} Mbps was marginal" : $"; {Join(listed)} Mbps were marginal";
        var carried = best is int chosen ? results.LastOrDefault(r => r.TargetMbps == chosen && r.Verdict == SweepVerdict.Clean)?.Sample.MeanVideoMbps : null;
        // The target is a budget; what the link was shown to carry is the video the encoder actually produced.
        var carriedText = best is int b2 && carried is double video
            ? video < b2 * FullTestShare
                ? $" Encoded video averaged {video:0} Mbps at that step, so higher rates were only lightly tested; play something heavier for a fuller test."
                : $" Encoded video averaged {video:0} Mbps at that step."
            : "";
        var summary = best is int clean
            ? $"Highest clean target: {clean} Mbps" + marginalText + (firstFailure is int f ? $"; {f} Mbps failed." : ".") + carriedText
            : marginal is int only ? $"No step was fully clean; {only} Mbps was the best marginal result." : "Every step showed link trouble; check the radio signal and dongle placement before streaming at higher rates.";
        var confirmed = best is int winner && results.Last(r => r.TargetMbps == winner && r.Verdict == SweepVerdict.Clean).Passes >= 2;
        return new(best, marginal, summary, carried, Confirmed: confirmed);
    }
}
