using SteamFrame.Core;

public sealed class StreamCodecTests
{
    [Theory]
    [InlineData("HEVC", "HEVC")]
    [InlineData("H.265", "HEVC")]
    [InlineData("AV1", "AV1")]
    [InlineData("H264", "H.264")]
    public void Running_codec_requires_verified_metadata_and_fresh_output(string codec, string expected)
    {
        var snapshot = new EncoderSnapshot { ProcessId = 100, Installed = true, Active = true,
            Codec = codec, CodecObserved = true, CodecSource = "accepted encoder codec", BitDepth = 8 };
        var status = StreamCodecStatus.From(snapshot);
        Assert.Equal(expected, status.Value); Assert.True(status.Verified);
        Assert.Contains("accepted encoder codec", status.Detail);
        Assert.Equal("Waiting", StreamCodecStatus.From(snapshot with { Active = false }).Value);
        Assert.Equal("Unknown", StreamCodecStatus.From(snapshot with { CodecObserved = false }).Value);
        Assert.Equal("Unknown", StreamCodecStatus.From(snapshot with { CodecReadFailed = true }).Value);
        var stopped = StreamCodecStatus.From(snapshot with { Installed = false, Active = false });
        Assert.Equal("Unknown", stopped.Value); Assert.Contains("Last verified codec", stopped.Detail);
        Assert.Equal("Unavailable", StreamCodecStatus.From(snapshot with { ProcessId = 0 }).Value);
    }

    [Fact]
    public void Loaded_av1_adapter_and_generic_nvenc_are_not_codec_confirmation()
    {
        var loaded = new EncoderSnapshot { ProcessId = 100, Installed = true,
            PairedCodecTrialPresent = true, Codec = "AV1", CodecObserved = false };
        Assert.False(StreamCodecStatus.From(loaded).Verified);
        Assert.NotEqual("AV1", StreamCodecStatus.From(loaded).Value);
        Assert.Equal("Unknown", StreamCodecStatus.From(loaded with { Active = true, Codec = "NVENC", CodecObserved = true }).Value);
    }

    [Fact]
    public void Cumulative_av1_output_is_a_baseline_then_expires_and_resets_on_restart()
    {
        var activity = new EncoderOutputActivity();
        Assert.False(activity.Sample("process-a", 12000, 100).Active);
        Assert.False(activity.Sample("process-a", 12000, 101).Active);
        var fresh = activity.Sample("process-a", 12120, 102);
        Assert.True(fresh.Active);
        Assert.True(activity.Sample("process-a", 12120, 103).Active);
        Assert.False(activity.Sample("process-a", 12120, 104).Active);
        Assert.False(activity.Sample("process-b", 1000, 105).Active);
        var next = activity.Sample("process-b", 1120, 106);
        Assert.True(next.Active); Assert.NotEqual(fresh.Generation, next.Generation);
        var reset = activity.Sample("process-b", 0, 107);
        Assert.False(reset.Active); Assert.NotEqual(next.Generation, reset.Generation);
        Assert.True(activity.Sample("process-b", 120, 108).Active);
        activity.Reset(); Assert.False(activity.Sample("process-b", 120, 109).Active);
    }
}
