namespace SteamLinkVRSettings.Tests;

public class StreamingHealthTests
{
    [Fact]
    public void StreamingLogCollectionIsBoundedSafeAndIncremental() => StreamingHealthHarness.Run();

    [Theory]
    [InlineData("Tue Oct 13 2026 12:29:56.264 [Info] - [Perf warning] 28.252ms - videonvenc::SubmitFrame", 28.252)]
    [InlineData("Tue Oct 13 2026 12:29:56.264 [Info] - [Perf warning] 4.5ms - StartEncodeData::nvEncReconfigureEncoder", 4.5)]
    [InlineData("Tue Oct 13 2026 12:29:56.264 [Info] - Perf warning: Getting from SubmitFrame to StartEncode took 115.959ms", 115.959)]
    public void Every_reported_encoder_stall_stage_is_counted(string line, double duration)
    {
        Assert.True(Frame.StreamingHealth.TryParseLine("driver_vrlink.txt", line, DateTimeOffset.Now, out var item));
        Assert.Equal(Frame.StreamingHealthCategory.EncoderPerformance, item!.Category);
        Assert.Equal(duration, item.DurationMs);
        Assert.True(item.TimestampFromLog);
    }

    [Fact]
    public void Frame_id_stream_resets_are_recorded()
    {
        const string line = "Tue Oct 13 2026 13:14:10.120 [Info] - Reset video stream because frame ID was not what we expected. wgp id (683) - last id (665) = diff: (18)";
        Assert.True(Frame.StreamingHealth.TryParseLine("driver_vrlink.txt", line, DateTimeOffset.Now, out var item));
        Assert.Equal(Frame.StreamingHealthCategory.StreamResetRequest, item!.Category);
        Assert.Equal(683, item.ResetRequestId); Assert.Equal(665, item.PreviousResetId);
    }

    [Fact]
    public void A_truncated_capture_record_is_terminated_before_appending()
    {
        var path = Path.Combine(Path.GetTempPath(), "slvr-capture-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllText(path, "{\"complete\":1}\n{\"cut\":");
            using (var stream = Frame.CaptureFile.OpenAppend(path)) stream.Write("{\"next\":2}\n"u8);
            Assert.Equal(new[] { "{\"complete\":1}", "{\"cut\":", "{\"next\":2}" }, File.ReadAllLines(path));
            using (var stream = Frame.CaptureFile.OpenAppend(path)) stream.Write("{\"last\":3}\n"u8);
            Assert.Equal(4, File.ReadAllLines(path).Length);
        }
        finally { File.Delete(path); }
    }
}
