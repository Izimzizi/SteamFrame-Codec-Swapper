using SteamLinkVRSettings.Frame;

/// <summary>Driver link statistics feeding Auto tune: held frames and SteamVR's own bitrate step-downs.</summary>
public class LinkEvidenceTests
{
    private static readonly BenchmarkCandidate Candidate = new(300, EncoderMultiPass.QuarterResolution, false);
    private static StreamingHealthSnapshot Health(DateTimeOffset at, long throttles = 0, double throttleMs = 0, long changes = 0, StreamingHealthEvent? change = null)
        => new(at, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, true, true, "", "", null, [], SenderThrottleEvents: throttles, SenderThrottleMs: throttleMs, BitrateChanges: changes, LatestBitrateChange: change);
    private static List<BenchmarkSample> Good(int seconds = 25, int fps = 90, double mbps = 240) => Enumerable.Range(0, seconds + 1).Select(i => new BenchmarkSample(
        100 + i, "session", (ulong)(i * fps), (ulong)(i * mbps * 1e6 / 8), 0, 0, 0, 0, 0, 2,
        new(1000 + i, "receiver", i * fps, i * fps, 0, 0), new(i + 1, i * fps, 0, 0, 0, fps), true, "Dedicated wireless") { PcTimeoutPresentations = 0 }).ToList();

    [Fact]
    public void A_step_down_during_the_trial_is_recorded_and_counts_as_a_serious_issue()
    {
        var start = DateTimeOffset.Now;
        var lowered = new StreamingHealthEvent(start.AddSeconds(10), start.AddSeconds(10), true, "driver_vrlink.txt", StreamingHealthCategory.NetworkBadLink, "x", 86.7, 0, 300, 225);
        var evidence = BenchmarkLogEvidence.Between(Health(start, throttles: 5, throttleMs: 40), Health(start.AddSeconds(25), throttles: 12, throttleMs: 98, changes: 1, change: lowered));
        Assert.Equal(7, evidence.HeldFrames); Assert.Equal(58, evidence.HeldFrameMs, 6);
        Assert.Equal(1, evidence.BitrateChanges); Assert.Equal(225, evidence.LoweredToMbps);
        Assert.True(evidence.LinkLoweredBelow(300)); Assert.False(evidence.LinkLoweredBelow(225));
        Assert.Contains("1 bitrate change, lowered to 225 Mbps", evidence.Summary); Assert.Contains("7 held frames", evidence.Summary);
        var clean = WirelessBenchmark.Evaluate(Candidate, Good(), 90, 25);
        Assert.True(clean.Passed); Assert.False(BenchmarkExplorer.Serious(clean, new()));
        var limited = clean with { Logs = evidence };
        Assert.True(limited.LinkLimited); Assert.True(BenchmarkExplorer.Serious(limited, new()));
        Assert.Contains("lowered the stream to 225 Mbps", limited.LinkNote);
        Assert.Contains("did not carry 300 Mbps", limited.LinkNote);
    }

    [Fact]
    public void An_earlier_step_down_or_a_reference_candidate_does_not_limit_the_trial()
    {
        var start = DateTimeOffset.Now;
        var stale = new StreamingHealthEvent(start.AddSeconds(-30), start.AddSeconds(-30), true, "driver_vrlink.txt", StreamingHealthCategory.NetworkBadLink, "x", 86.7, 0, 300, 225);
        // The counter did not advance during the trial, so an older change is not attributed to it.
        var evidence = BenchmarkLogEvidence.Between(Health(start, changes: 1, change: stale), Health(start.AddSeconds(25), changes: 1, change: stale));
        Assert.Equal(0, evidence.BitrateChanges); Assert.Null(evidence.LoweredToMbps); Assert.False(evidence.LinkLoweredBelow(300));
        var trial = WirelessBenchmark.Evaluate(Candidate, Good(), 90, 25) with { Logs = evidence };
        Assert.False(trial.LinkLimited); Assert.Null(trial.LinkNote); Assert.False(BenchmarkExplorer.Serious(trial, new()));
        var raised = new StreamingHealthEvent(start.AddSeconds(5), start.AddSeconds(5), true, "driver_vrlink.txt", StreamingHealthCategory.NetworkBadLink, "x", 10, 2, 150, 300);
        var recovered = BenchmarkLogEvidence.Between(Health(start), Health(start.AddSeconds(25), changes: 1, change: raised));
        Assert.Equal(1, recovered.BitrateChanges); Assert.Null(recovered.LoweredToMbps);
        var reference = WirelessBenchmark.Evaluate(Candidate with { ValveReference = true }, Good(), 90, 25) with { Logs = BenchmarkLogEvidence.Between(Health(start), Health(start.AddSeconds(25), changes: 1, change: stale with { Timestamp = start.AddSeconds(5), ObservedAt = start.AddSeconds(5) })) };
        Assert.False(reference.LinkLimited);
    }

    [Fact]
    public void Sustained_held_frames_are_noted_without_changing_the_verdict()
    {
        var start = DateTimeOffset.Now;
        var evidence = BenchmarkLogEvidence.Between(Health(start), Health(start.AddSeconds(25), throttles: 6, throttleMs: 50));
        var trial = WirelessBenchmark.Evaluate(Candidate, Good(), 90, 25) with { Logs = evidence };
        Assert.False(trial.LinkLimited); Assert.False(BenchmarkExplorer.Serious(trial, new()));
        Assert.Contains("held back 6 frames", trial.LinkNote);
        var few = trial with { Logs = BenchmarkLogEvidence.Between(Health(start), Health(start.AddSeconds(25), throttles: 2, throttleMs: 16)) };
        Assert.Null(few.LinkNote);
    }
}
