using SteamFrame.Core;

namespace SteamFrame.Tests;

public class StreamingHealthTests
{
    [Theory]
    [InlineData("vrcompositor.txt", "Tue Oct 13 2026 21:58:48.745 [Warning] - [Compositor] AcquireSync FAILED with WAIT_TIMEOUT")]
    [InlineData("vrserver.txt", "Tue Oct 13 2026 21:58:05.515 [Info] - vrlink: AcquireSync FAILED with WAIT_TIMEOUT")]
    public void Shared_texture_timeouts_are_detected_at_both_handoff_sides(string source, string line)
    {
        Assert.True(StreamingHealth.TryParseLine(source, line + " PRIVATE_SUFFIX", DateTimeOffset.Now, out var item));
        Assert.Equal(StreamingHealthCategory.CompositorAcquireTimeout, item!.Category);
        Assert.True(item.TimestampFromLog);
        Assert.Null(item.DurationMs); // Log does not state the actual wait duration.
        Assert.DoesNotContain("PRIVATE_SUFFIX", item.Message);
        Assert.Contains("before encoding", item.Message);
    }

    [Theory]
    [InlineData("vrlink: Reset video stream because we had a stream reset request [wgpReset:7 != lastReset:6]")]
    [InlineData("vrlink: [Perf warning] 72.306ms - videonvenc::GetEncodedPacket")]
    [InlineData("other-driver: AcquireSync FAILED with WAIT_TIMEOUT")]
    [InlineData("AcquireSync FAILED with WAIT_TIMEOUT")]
    [InlineData("vrlink: AcquireSync FAILED with WAIT_TIMEOUT_FAKE")]
    public void Server_tail_does_not_duplicate_driver_events_or_accept_other_sources(string body)
        => Assert.False(StreamingHealth.TryParseLine("vrserver.txt", body, DateTimeOffset.Now, out _));

    [Fact]
    public void Server_texture_warnings_are_collected_incrementally_without_replaying_existing_lines()
    {
        var root = Path.Combine(Path.GetTempPath(), "slvr-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "vrserver.txt");
            const string warning = "Tue Oct 13 2026 21:58:05.515 [Info] - vrlink: AcquireSync FAILED with WAIT_TIMEOUT\n";
            File.WriteAllText(path, warning);
            var health = new StreamingHealth(root, Path.Combine(root, "capture"));
            Assert.Equal(0, health.Poll(TestContext.Current.CancellationToken).TotalEvents);
            File.AppendAllText(path, warning);
            var snapshot = health.Poll(TestContext.Current.CancellationToken);
            Assert.True(snapshot.ServerLogAvailable);
            Assert.Equal(1, snapshot.CompositorAcquireTimeouts);
            Assert.Equal("vrserver.txt", Assert.Single(snapshot.RecentEvents).Source);
            Assert.Equal(1, health.Poll(TestContext.Current.CancellationToken).TotalEvents);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void StreamingLogCollectionIsBoundedSafeAndIncremental() => StreamingHealthHarness.Run();

    [Fact]
    public void Handoff_event_timeout_is_distinguished_from_failed_texture_ownership()
    {
        const string line = "WaitForAcquire timed out (NamedEvent); rendering the next frame before the driver took the sync texture [2700 messages suppressed]";
        Assert.True(StreamingHealth.TryParseLine("vrcompositor.txt", line, DateTimeOffset.Now, out var item));
        Assert.Equal(2700, item!.SuppressedCount);
        Assert.Null(item.DurationMs);
        Assert.Contains("handoff event", item.Message);
        Assert.Contains("does not prove shared-texture acquisition failed", item.Message);
    }

    [Theory]
    [InlineData("Tue Oct 13 2026 12:29:56.264 [Info] - [Perf warning] 28.252ms - videonvenc::SubmitFrame", 28.252)]
    [InlineData("Tue Oct 13 2026 12:29:56.264 [Info] - [Perf warning] 4.5ms - StartEncodeData::nvEncReconfigureEncoder", 4.5)]
    [InlineData("Tue Oct 13 2026 12:29:56.264 [Info] - Perf warning: Getting from SubmitFrame to StartEncode took 115.959ms", 115.959)]
    public void Every_reported_encoder_stall_stage_is_counted(string line, double duration)
    {
        Assert.True(StreamingHealth.TryParseLine("driver_vrlink.txt", line, DateTimeOffset.Now, out var item));
        Assert.Equal(StreamingHealthCategory.EncoderPerformance, item!.Category);
        Assert.Equal(duration, item.DurationMs);
        Assert.True(item.TimestampFromLog);
    }

    [Fact]
    public void Frame_id_stream_resets_are_recorded()
    {
        const string line = "Tue Oct 13 2026 13:14:10.120 [Info] - Reset video stream because frame ID was not what we expected. wgp id (683) - last id (665) = diff: (18)";
        Assert.True(StreamingHealth.TryParseLine("driver_vrlink.txt", line, DateTimeOffset.Now, out var item));
        Assert.Equal(StreamingHealthCategory.StreamResetRequest, item!.Category);
        Assert.Equal(683, item.ResetRequestId); Assert.Equal(665, item.PreviousResetId);
    }

    [Fact]
    public void A_truncated_capture_record_is_terminated_before_appending()
    {
        var path = Path.Combine(Path.GetTempPath(), "slvr-capture-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllText(path, "{\"complete\":1}\n{\"cut\":");
            using (var stream = CaptureFile.OpenAppend(path)) stream.Write("{\"next\":2}\n"u8);
            Assert.Equal(new[] { "{\"complete\":1}", "{\"cut\":", "{\"next\":2}" }, File.ReadAllLines(path));
            using (var stream = CaptureFile.OpenAppend(path)) stream.Write("{\"last\":3}\n"u8);
            Assert.Equal(4, File.ReadAllLines(path).Length);
        }
        finally { File.Delete(path); }
    }
}
