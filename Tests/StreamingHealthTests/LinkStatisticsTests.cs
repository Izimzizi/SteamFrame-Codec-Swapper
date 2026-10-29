using System.Text;
using SteamFrame.Core;

namespace SteamFrame.Tests;

/// <summary>The driver's own link statistics: estimate, capacity histogram, held frames and bitrate changes.</summary>
public class LinkStatisticsTests(ITestOutputHelper output)
{
    [Fact]
    public void A_bitrate_step_down_in_a_bad_link_event_is_described_with_its_holdoff_and_mode()
    {
        const string line = "Thu Oct 15 2026 12:50:24.406 [Info] - Bad link event: d=86.7 ms 200.0 mbit/s -> 150.0 mbit/s, ping 0.0 ms, holdoff 1, auto 0";
        Assert.True(StreamingHealth.TryParseLine("driver_vrlink.txt", line, DateTimeOffset.Now, out var item));
        Assert.Equal(StreamingHealthCategory.NetworkBadLink, item!.Category);
        Assert.True(item.ChangedBitrate);
        Assert.Equal(200, item.PreviousBitrateMbps); Assert.Equal(150, item.NewBitrateMbps);
        Assert.Equal(1, item.HoldoffSteps); Assert.False(item.Automatic);
        Assert.Contains("lowered the stream from 200 to 150 Mbps", item.Message);
        Assert.Contains("86.7 ms", item.Message);
    }

    [Fact]
    public void An_unchanged_bad_link_event_says_so_and_still_parses_without_the_newer_fields()
    {
        Assert.True(StreamingHealth.TryParseLine("driver_vrlink.txt", "Thu Oct 15 2026 12:58:00.178 [Info] - Bad link event: d=10.9 ms 400.0 mbit/s -> 400.0 mbit/s, ping 2.6 ms, holdoff 1, auto 0", DateTimeOffset.Now, out var newer));
        Assert.False(newer!.ChangedBitrate); Assert.Contains("not changed", newer.Message); Assert.Equal(1, newer.HoldoffSteps);
        Assert.True(StreamingHealth.TryParseLine("driver_vrlink.txt", "Bad link event: d=2574.7 ms 150.0 mbit/s -> 350.0 mbit/s, ping 3.0 ms", DateTimeOffset.Now, out var older));
        Assert.True(older!.ChangedBitrate); Assert.Null(older.HoldoffSteps); Assert.Null(older.Automatic);
        Assert.Contains("raised the stream from 150 to 350 Mbps", older.Message);
    }

    [Theory]
    [InlineData("Thu Oct 15 2026 12:58:09.491 [Info] - Link BW: Est 400.0 Mbit/s (auto: 0)", 400, false)]
    [InlineData("Link BW: Est 187.5 Mbit/s (auto: 1)", 187.5, true)]
    public void The_driver_link_estimate_carries_its_rate_and_automatic_flag(string line, double estimate, bool automatic)
    {
        Assert.True(StreamingHealth.TryParseLine("driver_vrlink.txt", line, DateTimeOffset.Now, out var item));
        Assert.Equal(StreamingHealthCategory.LinkEstimate, item!.Category);
        Assert.Equal(estimate, item.EstimateMbps); Assert.Equal(automatic, item.Automatic);
    }

    [Fact]
    public void The_link_capacity_histogram_is_parsed_and_summarized()
    {
        const string line = "Thu Oct 15 2026 12:46:50.306 [Info] - Link BW hist: w 5.000000  9,0,1,1,3,5,1,1,2,0,3,0,0,1,0,1,0,1,1,0,0,1,1,0,0,1,0,1,0,1,0,1,0,0,1,0,1,0,1,0,0,1,2,3,3,8,7,2,0,0,0,1,1,0,1,4,6,10,9,14,13,19,22,8,10,8,16,23,18,17,3160,";
        Assert.True(StreamingHealth.TryParseLine("driver_vrlink.txt", line, DateTimeOffset.Now, out var item));
        Assert.Equal(StreamingHealthCategory.LinkHistogram, item!.Category);
        var histogram = item.Histogram!;
        Assert.Equal(5, histogram.BucketMbps); Assert.Equal(71, histogram.Counts.Count);
        Assert.Equal(350, histogram.OverflowEdgeMbps);
        Assert.Equal(3425, histogram.TotalSamples); // 265 resolved frames plus 3160 in the open-ended bucket.
        Assert.True(histogram.OverflowShare > 0.9);
        var p10 = histogram.Percentile(0.10);
        Assert.True(p10.AtOrAbove); Assert.Equal(350, p10.Mbps);
        Assert.Contains("at least 350", item.Message);
        Assert.True(histogram.ShareBelow(100) < 0.02);
    }

    [Fact]
    public void A_histogram_of_a_slow_link_resolves_its_percentile_inside_the_buckets()
    {
        var counts = new long[71];
        counts[4] = 20; counts[8] = 40; counts[12] = 30; counts[70] = 10; // 20..60 Mbps with a few bursts.
        var histogram = new LinkCapacityHistogram(5, counts);
        Assert.Equal(100, histogram.TotalSamples);
        Assert.Equal((20, false), histogram.Percentile(0.10));
        Assert.Equal((40, false), histogram.Percentile(0.50));
        Assert.Equal((350, true), histogram.Percentile(0.95));
        Assert.Equal(0.9, histogram.ShareBelow(100), 6);
    }

    [Theory]
    [InlineData("Link BW hist: w 5.000000  1,2")]
    [InlineData("Link BW hist: w 0 1,2,3,")]
    [InlineData("Link BW hist: w 5.000000  1,")]
    [InlineData("Link BW hist: w 5.000000  1,x,3,")]
    [InlineData("Link BW: Est NaN Mbit/s (auto: 0)")]
    [InlineData("Link BW: Est 400.0 Mbit/s (auto: 2)")]
    [InlineData("THROTTLE EVENT: -5")]
    [InlineData("THROTTLE EVENT: 12345678901")]
    public void Malformed_link_statistics_are_rejected(string line) =>
        Assert.False(StreamingHealth.TryParseLine("driver_vrlink.txt", line, DateTimeOffset.Now, out _));

    [Fact]
    public void An_oversized_histogram_line_is_rejected()
    {
        var line = "Link BW hist: w 5.000000  " + string.Concat(Enumerable.Repeat("1,", 129));
        Assert.False(StreamingHealth.TryParseLine("driver_vrlink.txt", line, DateTimeOffset.Now, out _));
    }

    [Fact]
    public void Link_connection_changes_are_recorded_with_their_direction()
    {
        Assert.True(StreamingHealth.TryParseLine("driver_vrlink.txt", "Thu Oct 15 2026 14:39:33.382 [Info] - Connection active at 408.333159", DateTimeOffset.Now, out var active));
        Assert.Equal(StreamingHealthCategory.LinkConnection, active!.Category); Assert.True(active.LinkActive);
        Assert.True(StreamingHealth.TryParseLine("driver_vrlink.txt", "Thu Oct 15 2026 14:39:52.487 [Info] - Connection inactive at 427.439069", DateTimeOffset.Now, out var inactive));
        Assert.False(inactive!.LinkActive); Assert.Contains("inactive", inactive.Message);
        Assert.False(StreamingHealth.TryParseLine("driver_vrlink.txt", "Connection active at x", DateTimeOffset.Now, out _));
        var root = Path.Combine(Path.GetTempPath(), "SteamLinkVRSettings-health-link-state-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs"); Directory.CreateDirectory(logs);
        try
        {
            var driver = Path.Combine(logs, "driver_vrlink.txt");
            // An existing log seeds the last known link state without counting or capturing anything.
            File.WriteAllText(driver, "Thu Oct 15 2026 14:30:00.000 [Info] - Connection active at 100.0\nThu Oct 15 2026 14:33:00.000 [Info] - Connection inactive at 280.5\nThu Oct 15 2026 14:33:01.000 [Info] - THROTTLE EVENT: 8344\n", new UTF8Encoding(false));
            var seeded = new StreamingHealth(logs, Path.Combine(root, "capture-seeded"));
            Assert.False(seeded.Current.LinkActive); Assert.Equal(0, seeded.Current.TotalEvents);
            Assert.Equal(33, seeded.Current.LinkChangedAt!.Value.Minute);
            Assert.Equal(0, seeded.Poll().TotalEvents);
            File.Delete(driver);
            var health = new StreamingHealth(logs, Path.Combine(root, "capture"));
            Assert.Null(health.Current.LinkActive);
            File.WriteAllText(driver, "Thu Oct 15 2026 14:35:57.302 [Info] - Connection active at 192.253233\n", new UTF8Encoding(false));
            var state = health.Poll();
            Assert.True(state.LinkActive); Assert.NotNull(state.LinkChangedAt);
            Assert.Single(state.RecentEvents); // Link changes are notable events.
            File.AppendAllText(driver, "Thu Oct 15 2026 14:38:35.101 [Info] - Connection inactive at 350.052398\n");
            var later = health.Poll();
            Assert.False(later.LinkActive); Assert.True(later.LinkChangedAt >= state.LinkChangedAt);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Multi_second_holds_and_holds_while_the_link_is_down_are_stalls_not_held_frames()
    {
        var root = Path.Combine(Path.GetTempPath(), "SteamLinkVRSettings-health-stalls-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs"); Directory.CreateDirectory(logs);
        try
        {
            var driver = Path.Combine(logs, "driver_vrlink.txt");
            var health = new StreamingHealth(logs, Path.Combine(root, "capture"));
            File.WriteAllText(driver, string.Join("\n",
                "Thu Oct 15 2026 14:44:10.802 [Info] - Connection active at 685.754654",
                "Thu Oct 15 2026 14:44:12.000 [Info] - THROTTLE EVENT: 8344",
                "Thu Oct 15 2026 14:44:13.000 [Info] - THROTTLE EVENT: 36943000",
                "Thu Oct 15 2026 14:44:14.000 [Info] - THROTTLE EVENT: 1000000",
                "Thu Oct 15 2026 14:44:15.000 [Info] - Connection inactive at 690.0",
                "Thu Oct 15 2026 14:44:16.000 [Info] - THROTTLE EVENT: 300078",
                "Thu Oct 15 2026 14:44:17.000 [Info] - Connection active at 692.0",
                "Thu Oct 15 2026 14:44:18.000 [Info] - THROTTLE EVENT: 11111", "") , new UTF8Encoding(false));
            var state = health.Poll();
            Assert.Equal(2, state.SenderThrottleEvents); Assert.Equal(2, state.ThrottleEventsLastMinute);
            Assert.Equal(8.344 + 11.111, state.SenderThrottleMs, 3);
            Assert.Equal(3, state.LinkStalls); // 36.9 s, exactly 1 s, and 0.3 s while the link was down.
            Assert.Contains(state.RecentEvents, e => e.Category == StreamingHealthCategory.SenderThrottle && e.Message.Contains("stalled for 36.9 s"));
            Assert.Contains(state.RecentEvents, e => e.Message.Contains("while the video link was down"));
            Assert.Contains("3 sender stalls", state.Summary);
            Assert.True(state.LinkActive);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Throttle_events_are_counted_as_held_frames_in_milliseconds()
    {
        Assert.True(StreamingHealth.TryParseLine("driver_vrlink.txt", "Thu Oct 15 2026 12:47:08.262 [Info] - THROTTLE EVENT: 11175", DateTimeOffset.Now, out var item));
        Assert.Equal(StreamingHealthCategory.SenderThrottle, item!.Category);
        Assert.Equal(11.175, item.ThrottleMs!.Value, 6);
        // The server log mirrors driver lines; link statistics are only taken from the driver log.
        Assert.False(StreamingHealth.TryParseLine("vrserver.txt", "vrlink: THROTTLE EVENT: 11175", DateTimeOffset.Now, out _));
    }

    [Fact]
    public void Periodic_statistics_feed_snapshot_fields_without_flooding_the_recent_list()
    {
        var root = Path.Combine(Path.GetTempPath(), "SteamLinkVRSettings-health-link-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs"); Directory.CreateDirectory(logs);
        try
        {
            var driver = Path.Combine(logs, "driver_vrlink.txt");
            var health = new StreamingHealth(logs, Path.Combine(root, "capture"));
            var builder = new StringBuilder();
            for (var i = 0; i < 40; i++) builder.Append("Thu Oct 15 2026 12:47:08.262 [Info] - THROTTLE EVENT: 8344\n");
            builder.Append("Thu Oct 15 2026 12:50:24.406 [Info] - Bad link event: d=86.7 ms 200.0 mbit/s -> 150.0 mbit/s, ping 0.0 ms, holdoff 1, auto 0\n");
            builder.Append("Thu Oct 15 2026 12:52:14.000 [Info] - Link BW: Est 275.0 Mbit/s (auto: 0)\n");
            builder.Append("Thu Oct 15 2026 12:52:14.000 [Info] - Link BW hist: w 5.000000  " + string.Concat(Enumerable.Repeat("0,", 70)) + "2000,\n");
            File.WriteAllText(driver, builder.ToString(), new UTF8Encoding(false));
            var state = health.Poll();
            Assert.Equal(40, state.SenderThrottleEvents); Assert.Equal(40, state.ThrottleEventsLastMinute);
            Assert.Equal(40 * 8.344, state.SenderThrottleMs, 3); Assert.Equal(40 * 8.344, state.ThrottleMsLastMinute, 3);
            Assert.Equal(1, state.BitrateChanges); Assert.Equal(150, state.LatestBitrateChange!.NewBitrateMbps);
            Assert.Equal(1, state.LinkEstimates); Assert.Equal(1, state.LinkHistograms);
            Assert.Equal(275, state.LatestEstimate!.Mbps); Assert.False(state.LatestEstimate.Automatic);
            // The newest driver statement wins: the estimate line followed the step-down.
            Assert.Equal(275, state.DriverBitrate!.Mbps); Assert.Equal(StreamingHealthCategory.LinkEstimate, state.DriverBitrate.Source);
            Assert.Equal(1.0, state.LatestHistogram!.Histogram.OverflowShare);
            Assert.Single(state.RecentEvents); // Only the bitrate change is a notable event.
            Assert.Equal(StreamingHealthCategory.NetworkBadLink, state.RecentEvents[0].Category);
            Assert.Equal(43, state.TotalEvents);
            Assert.Contains("40 held frames", state.Summary); Assert.Contains("1 bitrate changes", state.Summary);
            var capture = File.ReadAllLines(health.CapturePath);
            Assert.Equal(43, capture.Length);
            Assert.Contains(capture, row => row.Contains("\"histogram\"") && row.Contains("\"bucketMbps\":5"));
            Assert.DoesNotContain(capture, row => row.Contains("Link BW hist: w"));
            File.AppendAllText(driver, "Thu Oct 15 2026 12:52:40.000 [Info] - Bad link event: d=12.0 ms 275.0 mbit/s -> 250.0 mbit/s, ping 2.6 ms, holdoff 2, auto 0\n");
            state = health.Poll();
            Assert.Equal(250, state.DriverBitrate!.Mbps); Assert.Equal(StreamingHealthCategory.NetworkBadLink, state.DriverBitrate.Source);
            Assert.Equal(2, state.BitrateChanges);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// Replays a real driver_vrlink.txt named by SLVR_DRIVER_LOG through the collector and prints what the
    /// Wireless link card would show. Skipped silently when the variable is unset; never touches Steam's own folder.
    /// </summary>
    [Fact]
    public void A_real_driver_log_replays_into_link_statistics()
    {
        var source = Environment.GetEnvironmentVariable("SLVR_DRIVER_LOG");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;
        var root = Path.Combine(Path.GetTempPath(), "SteamLinkVRSettings-health-replay-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs"); Directory.CreateDirectory(logs);
        try
        {
            var health = new StreamingHealth(logs, Path.Combine(root, "capture"));
            File.Copy(source, Path.Combine(logs, "driver_vrlink.txt"));
            StreamingHealthSnapshot state;
            long previous;
            do { previous = health.Current.TotalEvents; state = health.Poll(); } while (state.TotalEvents != previous);
            output.WriteLine(state.Summary);
            output.WriteLine($"estimates {state.LinkEstimates}, histograms {state.LinkHistograms}, throttles {state.SenderThrottleEvents} ({state.SenderThrottleMs:0} ms), bitrate changes {state.BitrateChanges}");
            if (state.LatestEstimate is { } estimate) output.WriteLine($"latest estimate {estimate.Mbps} Mbps automatic={estimate.Automatic} at {estimate.Timestamp:HH:mm:ss}");
            if (state.DriverBitrate is { } driver) output.WriteLine($"driver bitrate {driver.Mbps} Mbps from {driver.Source} at {driver.Timestamp:HH:mm:ss}");
            if (state.LatestBitrateChange is { } change) output.WriteLine($"latest change: {change.Message} at {change.Timestamp:HH:mm:ss}");
            if (state.LatestHistogram is { } histogram)
            {
                var h = histogram.Histogram; var p10 = h.Percentile(0.1); var median = h.Percentile(0.5);
                output.WriteLine($"histogram at {histogram.Timestamp:HH:mm:ss}: {h.TotalSamples} frames, {h.OverflowShare:P0} ≥{h.OverflowEdgeMbps} Mbps, P10 {(p10.AtOrAbove ? "≥" : "")}{p10.Mbps}, median {(median.AtOrAbove ? "≥" : "")}{median.Mbps}, below 100 {h.ShareBelow(100):P1}");
            }
            Assert.True(state.TotalEvents > 0);
            Assert.True(state.LinkEstimates > 0 && state.LinkHistograms > 0, "A streaming session's log carries estimate and histogram lines.");
        }
        finally { Directory.Delete(root, true); }
    }
}
