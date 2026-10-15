using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Tests;

public class WirelessAdvisorTests
{
    private static LinkCapacityHistogram Histogram(params (int Bucket, long Count)[] entries)
    {
        var counts = new long[71];
        foreach (var (bucket, count) in entries) counts[bucket] = count;
        return new(5, counts);
    }
    private static readonly WirelessLinkSnapshot GoodRadio = new(true, true, "Realtek 8832CU Wireless LAN WiFi 6 USB NIC For Valve", "frame_1", "6 GHz", 37, 6135, 160, "802.11ax", 2401.5, 2401.5, 90, -63, 0, 0, 0, 4000, DateTimeOffset.Now, "ok");

    [Fact]
    public void A_clean_patched_link_with_open_ended_capacity_is_told_to_step_up()
    {
        var advice = WirelessAdvisor.Advise(new()
        {
            CurrentTargetMbps = 300, DriverPatched = true, StreamActive = true, Radio = GoodRadio,
            Histogram = Histogram((70, 2392), (60, 50)), HistogramAgeSeconds = 10, ThrottleEventsLastMinute = 1, HeadsetLossRatio = 0, HeadsetLatencyMs = 6
        });
        Assert.True(advice.Headroom); Assert.False(advice.LinkLimited);
        Assert.Equal(350, advice.RecommendedMbps);
        Assert.Equal(AdviceConfidence.High, advice.Confidence);
        Assert.Contains(advice.Reasons, r => r.Contains("headroom"));
        Assert.Contains(advice.Reasons, r => r.Contains("above Valve's default"));
    }

    [Fact]
    public void Held_frames_and_a_step_down_mark_the_link_as_limited_and_cut_the_target()
    {
        var change = new StreamingHealthEvent(DateTimeOffset.Now, DateTimeOffset.Now, true, "driver_vrlink.txt", StreamingHealthCategory.NetworkBadLink, "x", 86.7, 0, 400, 300);
        var advice = WirelessAdvisor.Advise(new()
        {
            CurrentTargetMbps = 400, DriverPatched = true, StreamActive = true, Radio = GoodRadio,
            Histogram = Histogram((70, 2000), (60, 100)), HistogramAgeSeconds = 20, ThrottleEventsLastMinute = 40,
            LatestBitrateChange = change, BitrateChangeAgeSeconds = 30
        });
        Assert.True(advice.LinkLimited);
        Assert.Equal(300, advice.RecommendedMbps); // min(400 × 0.8 = 320, step-down 300, open-ended capacity 350)
        Assert.Contains(advice.Reasons, r => r.Contains("40 frames were held back"));
        Assert.Contains(advice.Reasons, r => r.Contains("lowered the stream from 400 to 300"));
    }

    [Fact]
    public void Occasional_held_frames_hold_the_target_and_a_one_percent_share_marks_the_link_as_limiting()
    {
        var baseInput = new WirelessAdvisorInput
        {
            CurrentTargetMbps = 270, DriverPatched = true, StreamActive = true, Radio = GoodRadio,
            Histogram = Histogram((70, 3300), (60, 100)), HistogramAgeSeconds = 20
        };
        // 16 held frames at 90 Hz is 0.3% of a minute's frames: ordinary radio jitter, still clean.
        var jitter = WirelessAdvisor.Advise(baseInput with { ThrottleEventsLastMinute = 16, ThrottleMsLastMinute = 140 });
        Assert.False(jitter.LinkLimited); Assert.True(jitter.Headroom);
        // 30 held frames (0.56%) is noticeable, not limiting.
        var occasional = WirelessAdvisor.Advise(baseInput with { ThrottleEventsLastMinute = 30, ThrottleMsLastMinute = 280 });
        Assert.False(occasional.LinkLimited); Assert.False(occasional.Headroom);
        Assert.Equal(270, occasional.RecommendedMbps);
        Assert.StartsWith("Keep 270 Mbps for now", occasional.Headline);
        Assert.Contains(occasional.Reasons, r => r.Contains("occasional held frames"));
        // The same count at 120 Hz is a smaller share, and under 0.5% it is clean.
        Assert.Contains(WirelessAdvisor.Advise(baseInput with { ThrottleEventsLastMinute = 30, RefreshHz = 120 }).Reasons, r => r.Contains("headroom"));
        var limiting = WirelessAdvisor.Advise(baseInput with { ThrottleEventsLastMinute = 120, ThrottleMsLastMinute = 1000 });
        Assert.True(limiting.LinkLimited); Assert.Equal(215, limiting.RecommendedMbps);
        Assert.Contains(limiting.Reasons, r => r.Contains("holding the stream back"));
        var longHolds = WirelessAdvisor.Advise(baseInput with { ThrottleEventsLastMinute = 4, ThrottleMsLastMinute = 1300 });
        Assert.True(longHolds.LinkLimited);
    }

    [Fact]
    public void The_stock_driver_ceiling_and_usb2_cap_bound_the_recommendation()
    {
        var advice = WirelessAdvisor.Advise(new()
        {
            CurrentTargetMbps = 225, DriverPatched = false, StreamActive = true, Radio = GoodRadio,
            Histogram = Histogram((70, 2500)), HistogramAgeSeconds = 5, ThrottleEventsLastMinute = 0
        });
        Assert.Equal(232, advice.RecommendedMbps);
        Assert.Contains(advice.Reasons, r => r.Contains("232 Mbps"));
        var usb2 = WirelessAdvisor.Advise(new()
        {
            CurrentTargetMbps = 400, DriverPatched = true, UsbSlow = true, StreamActive = true, Radio = GoodRadio,
            Histogram = Histogram((70, 2500)), HistogramAgeSeconds = 5, ThrottleEventsLastMinute = 0
        });
        Assert.Equal(250, usb2.RecommendedMbps);
        Assert.Contains(usb2.Reasons, r => r.Contains("USB 2"));
    }

    [Fact]
    public void A_resolved_slow_histogram_sets_the_target_below_its_tenth_percentile()
    {
        var advice = WirelessAdvisor.Advise(new()
        {
            CurrentTargetMbps = 200, DriverPatched = true, StreamActive = true, Radio = GoodRadio,
            Histogram = Histogram((4, 20), (8, 40), (12, 30), (70, 10)), HistogramAgeSeconds = 5, ThrottleEventsLastMinute = 6
        });
        // Clean right now, but the measured capacity floor is far below the target: min(target, 0.85 × P10 = 17 → floor 50).
        Assert.Equal(50, advice.RecommendedMbps); Assert.True(advice.LinkLimited);
        Assert.Contains(advice.Reasons, r => r.Contains("10th percentile"));
        Assert.Contains(advice.Reasons, r => r.Contains("above what it carried"));
    }

    [Fact]
    public void Radio_warnings_cap_the_target_and_explain_placement()
    {
        var weak = GoodRadio with { Rssi = -75, ReceiveRateMbps = 400, BandwidthMhz = 40 };
        var advice = WirelessAdvisor.Advise(new()
        {
            CurrentTargetMbps = 300, DriverPatched = true, StreamActive = true, Radio = weak,
            Histogram = Histogram((70, 2500)), HistogramAgeSeconds = 5, ThrottleEventsLastMinute = 0
        });
        Assert.Equal(200, advice.RecommendedMbps); // PHY 400 × 0.5
        Assert.Contains(advice.Reasons, r => r.Contains("weak"));
        Assert.Contains(advice.Reasons, r => r.Contains("40 MHz"));
        var legacy = GoodRadio with { Band = "2.4 GHz" };
        var legacyAdvice = WirelessAdvisor.Advise(new() { CurrentTargetMbps = 200, DriverPatched = true, StreamActive = true, Radio = legacy, Histogram = Histogram((70, 2500)), HistogramAgeSeconds = 5 });
        Assert.Equal(100, legacyAdvice.RecommendedMbps);
        Assert.Contains(legacyAdvice.Reasons, r => r.Contains("2.4 GHz"));
    }

    [Fact]
    public void Without_a_stream_the_advisor_waits_instead_of_guessing()
    {
        var advice = WirelessAdvisor.Advise(new() { CurrentTargetMbps = 200, DriverPatched = true, StreamActive = false });
        Assert.Null(advice.RecommendedMbps);
        Assert.Equal(AdviceConfidence.None, advice.Confidence);
        Assert.Contains(advice.Reasons, r => r.Contains("Start a wireless stream"));
        Assert.Null(WirelessAdvisor.Advise(new() { PairedCodec = true, StreamActive = true }).RecommendedMbps);
    }

    [Fact]
    public void Automatic_mode_reports_the_driver_estimate_and_at_the_ceiling_says_keep()
    {
        var advice = WirelessAdvisor.Advise(new()
        {
            CurrentTargetMbps = 500, AutomaticBitrate = true, DriverPatched = true, StreamActive = true, Radio = GoodRadio,
            DriverBitrate = new(412, true, DateTimeOffset.Now, DateTimeOffset.Now, StreamingHealthCategory.LinkEstimate),
            Histogram = Histogram((70, 2500)), HistogramAgeSeconds = 5, ThrottleEventsLastMinute = 0
        });
        Assert.Equal(500, advice.RecommendedMbps);
        Assert.Contains(advice.Reasons, r => r.Contains("own estimate is 412"));
        Assert.Contains(advice.Reasons, r => r.Contains("already at the 500 Mbps limit"));
        Assert.StartsWith("Keep 500 Mbps", advice.Headline);
    }

    [Theory]
    [InlineData(123.0, 125)]
    [InlineData(2.0, 25)]
    [InlineData(999.0, 500)]
    public void Recommendations_are_rounded_to_five_within_supported_range(double value, int expected) => Assert.Equal(expected, WirelessAdvisor.Round5(value));
}

public class LinkCapacitySweepTests
{
    private static SweepSample Sample(double seconds = 24, long throttles = 0, long changes = 0, double? lowest = null, double? loss = 0, double? latency = 5, long resets = 0, long retries = 0, double? video = null, long drops = 0)
        => new(seconds, throttles, throttles * 8.3, 0, changes, lowest, loss, latency, drops, video, 90, retries, resets);

    [Fact]
    public void The_plan_ascends_to_the_ceiling_without_a_tiny_last_step()
    {
        Assert.Equal([150, 200, 232], LinkCapacitySweep.Plan(150, 232));
        Assert.Equal([150, 200, 250, 300, 350, 400, 450, 500], LinkCapacitySweep.Plan(150, 500));
        Assert.Equal([200, 250, 300, 350, 400, 460], LinkCapacitySweep.Plan(200, 460));
        Assert.Equal([232], LinkCapacitySweep.Plan(232, 232));
        Assert.Equal([400, 500], LinkCapacitySweep.Plan(400, 500, 100));
    }

    [Fact]
    public void Steps_are_judged_on_held_frames_loss_resets_and_step_downs()
    {
        Assert.Equal(SweepVerdict.Clean, LinkCapacitySweep.Evaluate(200, Sample(video: 180)).Verdict);
        Assert.Equal(SweepVerdict.Clean, LinkCapacitySweep.Evaluate(200, Sample(throttles: 6)).Verdict); // 15 per minute: under 0.5% of 90 Hz frames
        var marginal = LinkCapacitySweep.Evaluate(300, Sample(throttles: 12)); // 30 per minute
        Assert.Equal(SweepVerdict.Marginal, marginal.Verdict); Assert.Contains("held frames per minute", marginal.Reason);
        var failed = LinkCapacitySweep.Evaluate(400, Sample(throttles: 44)); // 110 per minute: over 2% of frames
        Assert.Equal(SweepVerdict.Failed, failed.Verdict);
        // At 120 Hz the same count is a smaller share of frames.
        Assert.Equal(SweepVerdict.Marginal, LinkCapacitySweep.Evaluate(400, Sample(throttles: 44) with { MeanFps = 120 }).Verdict);
        Assert.Equal(SweepVerdict.Marginal, LinkCapacitySweep.Evaluate(400, Sample(throttles: 44) with { MeanFps = null }, 120).Verdict);
        // Long holds fail on held time even when the count is small.
        Assert.Equal(SweepVerdict.Failed, LinkCapacitySweep.Evaluate(400, new SweepSample(24, 3, 900, 0, 0, null, 0, 5, 0, null, 90, 0, 0)).Verdict);
        var lowered = LinkCapacitySweep.Evaluate(400, Sample(changes: 1, lowest: 300));
        Assert.Equal(SweepVerdict.Failed, lowered.Verdict); Assert.Contains("lowered the stream to 300", lowered.Reason);
        Assert.Equal(SweepVerdict.Failed, LinkCapacitySweep.Evaluate(400, Sample(loss: 0.02)).Verdict);
        Assert.Equal(SweepVerdict.Marginal, LinkCapacitySweep.Evaluate(400, Sample(loss: 0.005)).Verdict);
        Assert.Equal(SweepVerdict.Failed, LinkCapacitySweep.Evaluate(400, Sample(resets: 1)).Verdict);
        Assert.Equal(SweepVerdict.Clean, LinkCapacitySweep.Evaluate(400, Sample(latency: 45)).Verdict);
        Assert.Equal(SweepVerdict.Marginal, LinkCapacitySweep.Evaluate(400, Sample(latency: 65)).Verdict);
        Assert.Equal(SweepVerdict.Failed, LinkCapacitySweep.Evaluate(400, Sample(seconds: 3)).Verdict);
        var drops = LinkCapacitySweep.Evaluate(200, Sample(drops: 4));
        Assert.Equal(SweepVerdict.Clean, drops.Verdict); Assert.Contains("not counted against the link", drops.Reason);
        // Retry requests are listed but only a flood counts: a moving scene logs a few per step on a healthy link.
        var recovered = LinkCapacitySweep.Evaluate(150, Sample(retries: 8, video: 145));
        Assert.Equal(SweepVerdict.Clean, recovered.Verdict); Assert.Equal("Clean; encoded video averaged 145 Mbps. 8 receiver retry requests, recovered.", recovered.Reason);
        var flood = LinkCapacitySweep.Evaluate(150, Sample(retries: 30)); // 75 per minute
        Assert.Equal(SweepVerdict.Marginal, flood.Verdict); Assert.Contains("30 receiver retry requests (75 per minute)", flood.Reason);
    }

    [Fact]
    public void The_sweep_stops_after_a_failure_or_two_marginal_steps_and_concludes_the_highest_clean_rate()
    {
        var results = new List<SweepStepResult>
        {
            LinkCapacitySweep.Evaluate(200, Sample()),
            LinkCapacitySweep.Evaluate(250, Sample()),
            LinkCapacitySweep.Evaluate(300, Sample(throttles: 12)),
        };
        Assert.False(LinkCapacitySweep.ShouldStop(results));
        results.Add(LinkCapacitySweep.Evaluate(350, Sample(throttles: 13)));
        Assert.True(LinkCapacitySweep.ShouldStop(results));
        var conclusion = LinkCapacitySweep.Conclude(results);
        Assert.Equal(250, conclusion.BestCleanMbps); Assert.Equal(350, conclusion.HighestMarginalMbps);
        Assert.Contains("Highest clean target: 250 Mbps; 300 and 350 Mbps were marginal", conclusion.Summary);
        var failedFirst = new List<SweepStepResult> { LinkCapacitySweep.Evaluate(200, Sample(throttles: 44)) };
        Assert.True(LinkCapacitySweep.ShouldStop(failedFirst));
        Assert.Null(LinkCapacitySweep.Conclude(failedFirst).BestCleanMbps);
        Assert.Contains("Every step showed link trouble", LinkCapacitySweep.Conclude(failedFirst).Summary);
        // A clean step above an earlier failure is not trusted.
        var odd = new List<SweepStepResult> { LinkCapacitySweep.Evaluate(200, Sample()), LinkCapacitySweep.Evaluate(250, Sample(throttles: 44)), LinkCapacitySweep.Evaluate(300, Sample()) };
        Assert.Equal(200, LinkCapacitySweep.Conclude(odd).BestCleanMbps);
    }

    [Fact]
    public void A_marginal_step_is_rechecked_and_the_winner_is_confirmed_with_a_second_pass()
    {
        var first = LinkCapacitySweep.Evaluate(250, Sample(throttles: 12)); // 30 per minute: marginal at 90 Hz
        Assert.True(LinkCapacitySweep.NeedsRecheck(first, 0));
        Assert.False(LinkCapacitySweep.NeedsRecheck(first, LinkCapacitySweep.MaxRechecks));
        Assert.False(LinkCapacitySweep.NeedsRecheck(LinkCapacitySweep.Evaluate(250, Sample()), 0));
        Assert.True(LinkCapacitySweep.NeedsRecheck(LinkCapacitySweep.Evaluate(250, Sample(throttles: 44)), 0));
        // A failed first pass that does not repeat is softened to marginal: the sweep goes on, but the rate is not recommended by itself.
        var failedOnce = LinkCapacitySweep.Recheck(LinkCapacitySweep.Evaluate(200, Sample(loss: 0.02, resets: 2)), LinkCapacitySweep.Evaluate(200, Sample(video: 160)));
        Assert.Equal(SweepVerdict.Marginal, failedOnce.Verdict); Assert.Contains("first pass failed", failedOnce.Reason); Assert.Equal(2, failedOnce.Passes);
        Assert.False(LinkCapacitySweep.ShouldStop([LinkCapacitySweep.Evaluate(150, Sample()), failedOnce]));
        // The sweep went on and found clean steps above it; the summary still records the step that failed once.
        Assert.Equal("Highest clean target: 250 Mbps; 200 Mbps was marginal.", LinkCapacitySweep.Conclude([LinkCapacitySweep.Evaluate(150, Sample()), failedOnce, LinkCapacitySweep.Evaluate(250, Sample())]).Summary);
        Assert.Equal(SweepVerdict.Failed, LinkCapacitySweep.Recheck(LinkCapacitySweep.Evaluate(200, Sample(resets: 2)), LinkCapacitySweep.Evaluate(200, Sample(throttles: 12))).Verdict);
        Assert.Equal(SweepVerdict.Failed, LinkCapacitySweep.Recheck(LinkCapacitySweep.Evaluate(200, Sample(resets: 2)), LinkCapacitySweep.Evaluate(200, Sample(resets: 1))).Verdict);

        var cleared = LinkCapacitySweep.Recheck(first, LinkCapacitySweep.Evaluate(250, Sample(video: 190)));
        Assert.Equal(SweepVerdict.Clean, cleared.Verdict); Assert.Equal(2, cleared.Passes);
        Assert.Contains("Clean on the re-check", cleared.Reason); Assert.Contains("first pass saw: 30 held frames", cleared.Reason);
        Assert.False(LinkCapacitySweep.NeedsRecheck(cleared, 0));
        var still = LinkCapacitySweep.Recheck(first, LinkCapacitySweep.Evaluate(250, Sample(throttles: 13)));
        Assert.Equal(SweepVerdict.Marginal, still.Verdict); Assert.Equal(2, still.Passes); Assert.Contains("on the re-check", still.Reason);
        Assert.False(LinkCapacitySweep.NeedsRecheck(still, 0)); // never a third pass
        var worse = LinkCapacitySweep.Recheck(first, LinkCapacitySweep.Evaluate(250, Sample(throttles: 44)));
        Assert.Equal(SweepVerdict.Failed, worse.Verdict); Assert.Equal(44, worse.Sample.ThrottleEvents);
        Assert.Throws<ArgumentException>(() => LinkCapacitySweep.Recheck(first, LinkCapacitySweep.Evaluate(300, Sample())));

        var best = LinkCapacitySweep.Evaluate(300, Sample(video: 220));
        var confirmed = LinkCapacitySweep.Confirm(best, LinkCapacitySweep.Evaluate(300, Sample(video: 225)));
        Assert.Equal(SweepVerdict.Clean, confirmed.Verdict); Assert.Equal(2, confirmed.Passes); Assert.Contains("Confirmed clean on a second pass", confirmed.Reason);
        var downgraded = LinkCapacitySweep.Confirm(best, LinkCapacitySweep.Evaluate(300, Sample(throttles: 12)));
        Assert.Equal(SweepVerdict.Marginal, downgraded.Verdict); Assert.Contains("confirmation pass after a clean first pass", downgraded.Reason);
        Assert.Throws<ArgumentException>(() => LinkCapacitySweep.Confirm(best, LinkCapacitySweep.Evaluate(350, Sample())));

        // A downgraded winner moves the ceiling down a step, and the summary names every marginal target above it.
        var results = new List<SweepStepResult> { LinkCapacitySweep.Evaluate(200, Sample()), LinkCapacitySweep.Evaluate(250, Sample()), downgraded, LinkCapacitySweep.Evaluate(350, Sample(throttles: 12)) };
        var conclusion = LinkCapacitySweep.Conclude(results);
        Assert.Equal(250, conclusion.BestCleanMbps);
        Assert.Equal("Highest clean target: 250 Mbps; 300 and 350 Mbps were marginal.", conclusion.Summary);
        Assert.Null(conclusion.CarriedVideoMbps); Assert.False(conclusion.LightlyTested);
        Assert.Equal("Highest clean target: 200 Mbps; 250 Mbps was marginal; 300 Mbps failed.", LinkCapacitySweep.Conclude(
        [
            LinkCapacitySweep.Evaluate(200, Sample()), LinkCapacitySweep.Evaluate(250, Sample(throttles: 12)), LinkCapacitySweep.Evaluate(300, Sample(throttles: 44))
        ]).Summary);
    }

    [Fact]
    public void The_conclusion_says_how_much_video_the_clean_step_really_carried()
    {
        // A static scene fills a third of the budget: the summary says so and flags the result as lightly tested.
        var light = LinkCapacitySweep.Conclude([LinkCapacitySweep.Evaluate(300, Sample(video: 233)), LinkCapacitySweep.Evaluate(350, Sample(throttles: 20))]);
        Assert.Equal(300, light.BestCleanMbps); Assert.Equal(233, light.CarriedVideoMbps); Assert.True(light.LightlyTested);
        Assert.Equal("Highest clean target: 300 Mbps; 350 Mbps was marginal. Encoded video averaged 233 Mbps at that step, so higher rates were only lightly tested; play something heavier for a fuller test.", light.Summary);
        // A busy scene that nearly fills the target is a full test.
        var full = LinkCapacitySweep.Conclude([LinkCapacitySweep.Evaluate(300, Sample(video: 262))]);
        Assert.False(full.LightlyTested);
        Assert.Equal("Highest clean target: 300 Mbps. Encoded video averaged 262 Mbps at that step.", full.Summary);
        // The carried rate belongs to the clean step that won, not to a higher marginal one.
        var mixed = LinkCapacitySweep.Conclude([LinkCapacitySweep.Evaluate(200, Sample(video: 190)), LinkCapacitySweep.Evaluate(250, Sample(throttles: 12, video: 240))]);
        Assert.Equal(190, mixed.CarriedVideoMbps);
    }

    [Fact]
    public void Sweep_history_describes_how_much_the_link_varies()
    {
        var now = new DateTimeOffset(2026, 10, 15, 15, 30, 0, TimeSpan.FromHours(-7));
        var history = new List<SweepRecord>();
        Assert.Null(LinkCapacitySweep.DescribeHistory(history, now));
        LinkCapacitySweep.Record(history, new(now.AddMinutes(-40), 300, 120, "Highest clean target: 300 Mbps; 350 Mbps failed."));
        Assert.Null(LinkCapacitySweep.DescribeHistory(history, now));
        LinkCapacitySweep.Record(history, new(now.AddMinutes(-22), 200, 120, "Highest clean target: 200 Mbps; 250 and 300 Mbps were marginal."));
        LinkCapacitySweep.Record(history, new(now.AddMinutes(-7), 150, 120, "Highest clean target: 150 Mbps; 200 Mbps failed."));
        var spread = LinkCapacitySweep.DescribeHistory(history, now)!;
        Assert.StartsWith("The last 3 sweeps ranged from 150 to 300 Mbps (300 Mbps at ", spread);
        Assert.Contains("settle on 150 Mbps for a quiet stream, or use automatic bitrate starting at 300 Mbps", spread);
        // Old runs fall out of the week-long window; close agreement is reported as such.
        var steady = new List<SweepRecord> { new(now.AddDays(-9), 100, 90, ""), new(now.AddHours(-3), 250, 90, ""), new(now.AddHours(-1), 250, 90, "") };
        Assert.StartsWith("The last two sweeps agreed within a step: 250 Mbps at ", LinkCapacitySweep.DescribeHistory(steady, now));
        Assert.Null(LinkCapacitySweep.DescribeHistory(steady, now.AddDays(8)));
        // A spread of a step or more argues for automatic mode starting at the highest clean ceiling.
        Assert.Equal(300, LinkCapacitySweep.AutomaticStart(history, now));
        Assert.Null(LinkCapacitySweep.AutomaticStart([history[0]], now));
        Assert.Null(LinkCapacitySweep.AutomaticStart(history, now.AddDays(8)));
        Assert.Null(LinkCapacitySweep.AutomaticStart([new(now.AddHours(-2), 250, 90, ""), new(now.AddHours(-1), 250, 90, "")], now));
        Assert.Null(LinkCapacitySweep.AutomaticStart([new(now.AddHours(-2), 150, 90, ""), new(now.AddHours(-1), 500, 90, "", NoCleanStep: true)], now));
        // A sweep that found no clean step is left out of the range but mentioned.
        LinkCapacitySweep.Record(history, new(now.AddMinutes(-2), 200, 120, "No step was fully clean; 200 Mbps was the best marginal result.", NoCleanStep: true));
        var withGap = LinkCapacitySweep.DescribeHistory(history, now)!;
        Assert.StartsWith("The last 3 sweeps ranged from 150 to 300 Mbps (", withGap); Assert.Contains(" One sweep found no clean step at all. This link changes", withGap);
        var onlyGaps = new List<SweepRecord> { new(now.AddHours(-2), 250, 90, "", NoCleanStep: true), new(now.AddHours(-1), 300, 90, "") };
        Assert.Equal("The last 2 sweeps disagreed. One sweep found no clean step at all. Sweep again before trusting any single result.", LinkCapacitySweep.DescribeHistory(onlyGaps, now));
        // The list is capped.
        var many = new List<SweepRecord>();
        for (var i = 0; i < LinkCapacitySweep.HistoryLimit + 5; i++) LinkCapacitySweep.Record(many, new(now.AddMinutes(-i), 200 + i, 90, ""));
        Assert.Equal(LinkCapacitySweep.HistoryLimit, many.Count); Assert.Equal(205, many[0].CeilingMbps);
    }

    [Fact]
    public async Task Sweep_history_survives_a_restart_and_is_seeded_from_an_older_single_result()
    {
        var folder = Path.Combine(Path.GetTempPath(), "slvr-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "frame.json");
            var preferences = new FramePreferences { LastSweepCeilingMbps = 300, LastSweepAt = new DateTimeOffset(2026, 10, 15, 14, 52, 0, TimeSpan.FromHours(-7)), LastSweepRefreshHz = 120, LastSweepSummary = "Highest clean target: 300 Mbps." };
            await preferences.SaveAsync(path);
            var reloaded = FramePreferences.Load(path);
            var seeded = Assert.Single(reloaded.SweepHistory);
            Assert.Equal(300, seeded.CeilingMbps); Assert.Equal(120, seeded.RefreshHz);
            LinkCapacitySweep.Record(reloaded.SweepHistory, new(seeded.At.AddMinutes(16), 200, 120, "Highest clean target: 200 Mbps."));
            await reloaded.SaveAsync(path);
            var again = FramePreferences.Load(path);
            Assert.Equal([300, 200], again.SweepHistory.Select(r => r.CeilingMbps).ToArray());
            Assert.Equal(seeded.At, again.SweepHistory[0].At);
            Assert.All(again.SweepHistory, r => Assert.False(r.NoCleanStep)); // records written before the flag read as clean sweeps
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Only_presets_the_sweep_named_itself_are_replaced()
    {
        var name = LinkCapacitySweep.MeasuredPresetName(300, new DateTime(2026, 10, 15, 14, 52, 0));
        Assert.Equal("Measured ceiling 300 Mbps 10-15 14-52", name);
        Assert.True(LinkCapacitySweep.IsMeasuredPresetName(name));
        Assert.True(LinkCapacitySweep.IsMeasuredPresetName("Measured ceiling 75 Mbps 01-02 03-04"));
        Assert.False(LinkCapacitySweep.IsMeasuredPresetName("Measured ceiling 300 Mbps 10-15 14-52 (living room)"));
        Assert.False(LinkCapacitySweep.IsMeasuredPresetName("My 300 Mbps"));
        Assert.False(LinkCapacitySweep.IsMeasuredPresetName("measured ceiling 300 Mbps 10-15 14-52"));
    }
}
