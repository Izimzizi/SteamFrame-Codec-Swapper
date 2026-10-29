using SteamFrameTray;
using SteamFrame.Core;

namespace TrayTests;

public sealed class StatsSamplerTests
{
    private static EncoderSnapshot Snap(double seconds, ulong bytes, ulong frames, string codec = "H.264", bool active = true, bool installed = true, bool observed = true, int pid = 100) => new()
    {
        ProcessId = pid, Installed = installed, Active = active, Supported = true, Generation = "g1", Codec = codec, CodecObserved = observed,
        EncodedBytes = bytes, EncodedFrames = frames, TimestampSeconds = seconds, CodecSource = "paired adapter"
    };

    [Fact]
    public async Task Bitrate_and_fps_come_from_counter_deltas_and_the_first_sample_is_a_gap()
    {
        var queue = new Queue<EncoderSnapshot>([Snap(10, 0, 0), Snap(11, 50_000_000, 90), Snap(12, 100_000_000, 180)]);
        var sampler = new StatsSampler(() => Task.FromResult(queue.Dequeue()), () => throw new InvalidOperationException());
        var first = await sampler.SampleAsync();
        Assert.Null(first.Mbps);
        var second = await sampler.SampleAsync();
        Assert.Equal(400, second.Mbps!.Value, 3); Assert.Equal(90, second.Fps!.Value, 3);
        Assert.True(second.CodecVerified); Assert.Equal("H.264", second.Codec);
        await sampler.SampleAsync();
        Assert.Equal<double?>([null, 400, 400], sampler.History);
    }

    [Fact]
    public async Task The_average_covers_the_last_60_readings_and_skips_gaps()
    {
        var snapshots = new List<EncoderSnapshot> { Snap(0, 0, 0) };
        for (var i = 1; i <= 70; i++) snapshots.Add(Snap(i, (ulong)i * (i <= 10 ? 12_500_000UL : 25_000_000UL), (ulong)i * 72));
        var queue = new Queue<EncoderSnapshot>(snapshots);
        var sampler = new StatsSampler(() => Task.FromResult(queue.Dequeue()), () => throw new InvalidOperationException());
        Assert.Null(sampler.RecentAverage);
        await sampler.SampleAsync();
        Assert.Null(sampler.RecentAverage);
        while (queue.Count > 0) await sampler.SampleAsync();
        Assert.Equal(StatsSampler.HistoryLength, sampler.History.Count);
        Assert.Equal(sampler.History.Where(v => v is not null).Average(), sampler.RecentAverage);
    }

    [Fact]
    public async Task Inactive_output_is_a_gap_not_a_zero_and_restarts_the_baseline()
    {
        var queue = new Queue<EncoderSnapshot>([Snap(1, 0, 0), Snap(2, 10_000_000, 90), Snap(3, 10_000_000, 90, active: false), Snap(4, 20_000_000, 180)]);
        var sampler = new StatsSampler(() => Task.FromResult(queue.Dequeue()), () => throw new InvalidOperationException());
        await sampler.SampleAsync(); await sampler.SampleAsync();
        var stalled = await sampler.SampleAsync();
        Assert.Null(stalled.Mbps); Assert.Equal("Waiting", stalled.Codec); Assert.False(stalled.CodecVerified);
        var resumed = await sampler.SampleAsync();
        Assert.Null(resumed.Mbps);
    }

    [Fact]
    public async Task SteamVR_not_running_is_reported_and_history_is_capped()
    {
        var sampler = new StatsSampler(() => Task.FromResult(new EncoderSnapshot { Message = "SteamVR is not running." }), () => throw new InvalidOperationException());
        StatsSample? last = null;
        for (var i = 0; i < StatsSampler.HistoryLength + 5; i++) last = await sampler.SampleAsync();
        Assert.False(last!.SteamVrRunning); Assert.Equal("Unavailable", last.Codec);
        Assert.Equal(StatsSampler.HistoryLength, sampler.History.Count);
    }

    [Fact]
    public async Task Stock_HEVC_monitoring_is_started_once_per_SteamVR_run_with_a_retry_delay()
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var sampler = new StatsSampler(
            () => Task.FromResult(Snap(5, 0, 0, codec: "HEVC", installed: false, observed: false)),
            () => { starts++; throw new UnauthorizedAccessException("denied"); }, () => now);
        var first = await sampler.SampleAsync();
        Assert.Equal(1, starts); Assert.Contains("administrator", first.Note);
        await sampler.SampleAsync();
        Assert.Equal(1, starts);
        now += TimeSpan.FromSeconds(16);
        await sampler.SampleAsync();
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task The_paired_H264_backend_is_read_without_starting_the_HEVC_monitor()
    {
        var starts = 0;
        var sampler = new StatsSampler(() => Task.FromResult(Snap(5, 0, 0) with { PairedCodecTrialPresent = true }), () => { starts++; return Task.FromResult(new EncoderSnapshot()); });
        var sample = await sampler.SampleAsync();
        Assert.Equal(0, starts); Assert.Equal("H.264", sample.Codec);
    }

    [Fact]
    public async Task A_failed_read_is_unverified_not_a_crash()
    {
        var sampler = new StatsSampler(() => throw new IOException("busy"), () => throw new InvalidOperationException());
        var sample = await sampler.SampleAsync();
        Assert.False(sample.CodecVerified); Assert.Null(sample.Mbps);
    }
}
