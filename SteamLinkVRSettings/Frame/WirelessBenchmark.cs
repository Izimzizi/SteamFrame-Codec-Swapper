using System.Text.Json;

namespace SteamLinkVRSettings.Frame;

public sealed record BenchmarkOptions(int MaximumMbps = 450, int TrialSeconds = 10,
    int ValidationSeconds = 35, int HeadroomPercent = 10, bool FullMultipass = true, bool Unattended = true,
    // Retained serialized option name: now enables the full independent region comparison.
    bool AllowQualityReduction = true, bool TestResolutions = true, bool TestRefreshRates = true,
    double IssuePercent = 5, int IssueMilliseconds = 500)
{
    public void Validate()
    {
        if (MaximumMbps is < 100 or > 500 || TrialSeconds is < 8 or > 60 ||
            ValidationSeconds is < 20 or > 300 || HeadroomPercent is < 5 or > 25 ||
            !double.IsFinite(IssuePercent) || IssuePercent is < 1 or > 15 || IssueMilliseconds is < 100 or > 2000)
            throw new ArgumentException("Use a 100–500 Mbps ceiling, 8–60 second screens, 20–300 second confirmations, 5–25% headroom, 1–15% affected-frame threshold and 100–2000 ms hitch threshold.");
    }
}

public sealed record BenchmarkQuality(bool Enabled, int GazeOffset, int PeripheralOffset)
{
    public bool IsValid => GazeOffset is >= -20 and <= 12 && PeripheralOffset is >= -12 and <= 31;
    public string Label => Enabled ? $"gaze {GazeOffset:+0;-0;0}, periphery {PeripheralOffset:+0;-0;0}" : "Valve quality map";
    public static BenchmarkQuality From(EncoderSnapshot snapshot) => new(snapshot.Enabled, snapshot.CenterOffset, snapshot.PeripheralOffset);
    public bool Matches(EncoderSnapshot snapshot) => this == From(snapshot);
}

public sealed record BenchmarkCandidate(int TargetMbps, EncoderMultiPass MultiPass, bool AsyncSend, BenchmarkQuality? Quality = null)
{
    public int Width { get; init; }
    public int Fps { get; init; }
    // Confirm play settings from a fresh encoder, not a previous trial's RC state.
    public bool FreshSession { get; init; }
    public bool ValveReference { get; init; }
    public string Label => ValveReference ? "Valve encoding reference · automatic bitrate" :
        (Width>0 ? $"{Width}px stream width · {Fps} Hz · " : "")+$"{TargetMbps} Mbps · {MultiPass} · {(AsyncSend ? "asynchronous" : "synchronous")} send" +
        (Quality is null ? "" : " · " + Quality.Label);
}

// Receiver counters are compositor observations, not physical panel measurements.
public sealed record BenchmarkReceiver(double Seconds, string Identity, long Submits, long Presents, long Drops, long Timeouts);
public sealed record BenchmarkScene(double Seconds, long Frames, long InvalidPoses, long SubmitErrors, long LongIntervals, int Fps)
{
    public bool Tracked { get; init; } = true;
    public bool Unattended { get; init; }
    public double LongestIntervalMs { get; init; }
    public double LongWaitMs { get; init; }
    public double LongWorkMs { get; init; }
}
public sealed record BenchmarkSample(double Seconds, string Identity, ulong Frames, ulong Bytes, ulong Errors,
    long PcDrops, long PcTimeouts, long PcLate, long HistoryGaps, double GpuP99Ms,
    BenchmarkReceiver Receiver, BenchmarkScene Scene, bool ConfigurationAccepted, string WirelessPath)
{
    // Separate native counters: entering timeout state is not the number of
    // presentations made in that state. Null identifies older reports.
    public long? PcTimeoutPresentations { get; init; }
}
public sealed record BenchmarkTrial(BenchmarkCandidate Candidate, bool Passed, string Reason, double Seconds,
    double ActualMbps, double EncodedFps, double ReceiverFps, double GpuP99Ms, IReadOnlyList<BenchmarkSample> Samples)
{
    // Legacy flag: below the performance-warning thresholds, not a qualification
    // gate. Valid observations remain eligible through relative scoring.
    public BenchmarkAssessment? Assessment { get; init; }
    public BenchmarkQuantizer? Quantizer { get; init; }
    public string Stage { get; init; } = "Screen";
    public bool MeasurementValid => Assessment?.MeasurementValid ?? Passed;
    public BenchmarkLogEvidence? Logs { get; init; }
    public BenchmarkReceiverLogEvidence? ReceiverLogs { get; init; }
    public string? SceneTimingNote => Samples.Count > 1 && Samples[^1].Scene.LongIntervals > Samples[0].Scene.LongIntervals
        ? $"Scene submission spacing varied {Samples[^1].Scene.LongIntervals-Samples[0].Scene.LongIntervals} time(s). Presentation counters and delivery cadence were checked separately." : null;
    public string? BitrateNote => Seconds >= 3 && ActualMbps > Candidate.TargetMbps * 1.25
        ? "Video substantially exceeded the requested bitrate. Aggressive quality offsets can prevent difficult frames from shrinking enough, even with CBR. Compare gentler quality offsets." : null;
    /// <summary>SteamVR lowered the stream below this candidate's target during the trial, so the measured window did not run at the requested rate.</summary>
    public bool LinkLimited => !Candidate.ValveReference && Logs?.LinkLoweredBelow(Candidate.TargetMbps) == true;
    public string? LinkNote => LinkLimited ? $"SteamVR lowered the stream to {Logs!.LoweredToMbps:0} Mbps during this trial; the link did not carry {Candidate.TargetMbps} Mbps."
        : Logs is { HeldFrames: > 0 } logs && Seconds > 0 && logs.HeldFrames * 60 / Seconds >= 10 ? $"The sender held back {logs.HeldFrames} frames in {Seconds:0} s; the link was saturated at this rate." : null;
}

public sealed record BenchmarkReceiverLogEvidence(long RetryRequests, long RequestedPackets, long BacktrackResets,
    long UnrecoverableErrors, long DecoderQueueFailures, bool Complete)
{
    public static BenchmarkReceiverLogEvidence Between(FrameReceiverSnapshot before, FrameReceiverSnapshot after) => new(
        Math.Max(0,after.RetryEvents-before.RetryEvents), Math.Max(0,after.RequestedRetryPackets-before.RequestedRetryPackets),
        Math.Max(0,after.BacktrackWindowResets-before.BacktrackWindowResets), Math.Max(0,after.UnrecoverableErrors-before.UnrecoverableErrors),
        Math.Max(0,after.DecoderQueueFailures-before.DecoderQueueFailures), before.Available && after.Available &&
        before.StreamPid==after.StreamPid && before.StreamStartTicks==after.StreamStartTicks && before.LogGeneration==after.LogGeneration &&
        before.SkippedBytes==after.SkippedBytes && before.DiscardedLines==after.DiscardedLines);
    public string Summary => $"Headset log: {RetryRequests} retry requests ({RequestedPackets} packets requested), " +
        $"{BacktrackResets} backtrack resets, {UnrecoverableErrors} unrecoverable errors, {DecoderQueueFailures} decoder queue failures." +
        (Complete ? "" : " Headset log coverage was incomplete.");
}

// Log observations explain a failure without replacing measured presentation counters.
// Suppressed log messages are not converted into dropped-frame counts.
public sealed record BenchmarkLogEvidence(long RecoveryRequests, long NetworkDelays, long EncoderWarnings,
    long CompositorAcquireTimeouts, long EncoderReconfigurations, bool Complete)
{
    /// <summary>Frames the sender held back because the link could not take them yet (driver THROTTLE EVENT lines).</summary>
    public long HeldFrames { get; init; }
    public double HeldFrameMs { get; init; }
    /// <summary>Bad-link events in which SteamVR moved the stream to a different bitrate during the trial.</summary>
    public long BitrateChanges { get; init; }
    /// <summary>The lowest rate SteamVR said it was sending at during the trial, when it lowered the stream.</summary>
    public double? LoweredToMbps { get; init; }
    public static BenchmarkLogEvidence Between(StreamingHealthSnapshot before, StreamingHealthSnapshot after) => new(
        Math.Max(0,after.StreamResetRequests-before.StreamResetRequests), Math.Max(0,after.NetworkBadLinkEvents-before.NetworkBadLinkEvents),
        Math.Max(0,after.EncoderPerformanceWarnings-before.EncoderPerformanceWarnings), Math.Max(0,after.CompositorAcquireTimeouts-before.CompositorAcquireTimeouts),
        Math.Max(0,after.EncoderReconfigurations-before.EncoderReconfigurations),
        before.DriverLogAvailable && before.CompositorLogAvailable && after.DriverLogAvailable && after.CompositorLogAvailable &&
        after.SkippedBytes==before.SkippedBytes && after.DiscardedLines==before.DiscardedLines)
    {
        HeldFrames=Math.Max(0,after.SenderThrottleEvents-before.SenderThrottleEvents),
        HeldFrameMs=Math.Max(0,after.SenderThrottleMs-before.SenderThrottleMs),
        BitrateChanges=Math.Max(0,after.BitrateChanges-before.BitrateChanges),
        LoweredToMbps=after.BitrateChanges>before.BitrateChanges && after.LatestBitrateChange is {ChangedBitrate:true} change &&
            change.ObservedAt>before.ObservedAt && change.NewBitrateMbps<change.PreviousBitrateMbps ? change.NewBitrateMbps : null
    };
    /// <summary>True when SteamVR itself lowered the stream below the candidate's target: the link did not carry it.</summary>
    public bool LinkLoweredBelow(int targetMbps) => LoweredToMbps is double lowered && lowered < targetMbps - 1;
    public string Summary => $"Logs during this trial: {RecoveryRequests} recovery requests, {NetworkDelays} bad-link warnings" +
        (BitrateChanges>0 ? $" ({BitrateChanges} bitrate change{(BitrateChanges==1?"":"s")}{(LoweredToMbps is double l ? $", lowered to {l:0} Mbps" : "")})" : "") +
        $", {HeldFrames} held frames{(HeldFrames>0 ? $" ({HeldFrameMs:0} ms)" : "")}, " +
        $"{EncoderWarnings} encoder warnings, {CompositorAcquireTimeouts} compositor handoff timeouts." +
        (Complete ? "" : " Log coverage was incomplete.");
}
public sealed record BenchmarkResult(BenchmarkTrial? Winner, IReadOnlyList<BenchmarkTrial> Trials, string Message)
{
    public IReadOnlyList<BenchmarkRecommendation> Recommendations { get; init; } = [];

    public BenchmarkTrial? Reference { get; init; }
    public string? BaselineNote { get; init; }
}

public sealed record BenchmarkRecommendation(string Name, BenchmarkCandidate Candidate, BenchmarkTrial? Trial, string Note)
{
    public bool Measured => Trial?.MeasurementValid == true;
}

/// <summary>Pure, conservative policy: absent/changing evidence can never produce a passing preset.</summary>
public static class WirelessBenchmark
{
    public static BenchmarkTrial Evaluate(BenchmarkCandidate candidate, IReadOnlyList<BenchmarkSample> samples, int fps, int requiredSeconds)
        => BenchmarkScoring.Evaluate(candidate, samples, fps, requiredSeconds);

    public static IEnumerable<BenchmarkTrial> Rank(IEnumerable<BenchmarkTrial> trials) => BenchmarkScoring.Rank(trials);

    public static Task<BenchmarkResult> RunAsync(BenchmarkOptions options, int ceiling, bool initialAsync,
        Func<BenchmarkCandidate, int, CancellationToken, Task<BenchmarkTrial>> measure,
        Action<string> progress, CancellationToken token, Action<BenchmarkTrial>? trialCompleted = null,
        BenchmarkQuality? initialQuality = null, BenchmarkCandidate? valveReference = null,
        int initialWidth = 1664, int initialFps = 90)
        => BenchmarkExplorer.RunAsync(options,ceiling,initialAsync,measure,progress,token,trialCompleted,initialQuality,initialWidth,initialFps);
    public static TuningProfile Profile(TuningProfile baseline, BenchmarkCandidate candidate, int width, int fps) => baseline with
    {
        SavedAt = DateTimeOffset.Now, Codec = StreamCodec.Hevc, TargetMbps = candidate.TargetMbps, Fps = fps,
        MultiPass = candidate.MultiPass, Reapply = true,
        QualityEnabled = candidate.Quality?.Enabled ?? baseline.QualityEnabled,
        GazeOffset = candidate.Quality?.GazeOffset ?? baseline.GazeOffset,
        PeripheralOffset = candidate.Quality?.PeripheralOffset ?? baseline.PeripheralOffset,
        Driver = new(baseline.Driver)
        {
            ["targetBandwidth"] = JsonSerializer.SerializeToElement((double)candidate.TargetMbps),
            ["automaticBandwidth"] = JsonSerializer.SerializeToElement(false),
            ["automaticStreamFormatWidth"] = JsonSerializer.SerializeToElement(false),
            ["streamFormatWidth"] = JsonSerializer.SerializeToElement(width),
            ["asyncSend"] = JsonSerializer.SerializeToElement(candidate.AsyncSend)
        },
        Video = new(baseline.Video) { ["preferredRefreshRate"] = JsonSerializer.SerializeToElement((double)fps) }
    };
}
