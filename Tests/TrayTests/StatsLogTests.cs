using SteamFrameTray;
using SteamFrame.Core;

namespace TrayTests;

public sealed class StatsLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "runlog-" + Guid.NewGuid().ToString("N"));

    private static EncoderSnapshot Snap(double seconds, ulong bytes, ulong frames, ulong qpSum = 0, ulong qpSamples = 0, EncodingPolicySnapshot? policy = null) => new()
    {
        ProcessId = 100, Installed = true, Active = true, Supported = true, Generation = "g1", Codec = "H.264", CodecObserved = true,
        EncodedBytes = bytes, EncodedFrames = frames, TimestampSeconds = seconds, CodecSource = "paired adapter",
        QuantizerSum = qpSum, QuantizerSamples = qpSamples, LastFrameQp = 28, LastFrameBytes = 90_000, EncodingPolicy = policy
    };

    [Fact]
    public async Task The_encoders_configured_rates_and_the_average_quantizer_are_reported()
    {
        var policy = new EncodingPolicySnapshot { Observed = true, AverageBitrate = 500_000_000, MaxBitrate = 520_000_000, RateControl = 2, VbvSize = 7_000_000, FrameRateNum = 72, FrameRateDen = 1, Preset = "P4" };
        var queue = new Queue<EncoderSnapshot>([Snap(1, 0, 0, 0, 0, policy), Snap(2, 40_000_000, 72, 2000, 100, policy), Snap(3, 80_000_000, 144, 5000, 200, policy)]);
        var sampler = new StatsSampler(() => Task.FromResult(queue.Dequeue()), () => throw new InvalidOperationException());
        await sampler.SampleAsync();
        var second = await sampler.SampleAsync();
        Assert.Null(second.AverageQp);
        var third = await sampler.SampleAsync();
        Assert.Equal(30, third.AverageQp!.Value, 3);
        var config = third.Config!;
        Assert.Equal(500, config.AverageMbps); Assert.Equal(520, config.MaxMbps); Assert.Equal("CBR", config.RateControl); Assert.Equal(72, config.Fps);
    }

    [Fact]
    public async Task Missing_encoder_policy_and_failing_health_do_not_break_sampling()
    {
        var sampler = new StatsSampler(() => Task.FromResult(Snap(1, 0, 0)), () => throw new InvalidOperationException(), health: () => throw new IOException("log gone"));
        var sample = await sampler.SampleAsync();
        Assert.Null(sample.Config); Assert.Null(sample.Health);
    }

    [Fact]
    public async Task Dropped_frame_counts_come_from_the_health_reader()
    {
        var reading = new HealthReading(12, 3, 0, true, 5, 2, 1);
        var sampler = new StatsSampler(() => Task.FromResult(Snap(1, 0, 0)), () => throw new InvalidOperationException(), health: () => Task.FromResult<HealthReading?>(reading));
        Assert.Equal(reading, (await sampler.SampleAsync()).Health);
    }

    [Fact]
    public async Task The_run_log_writes_its_setup_then_one_row_per_sample_that_can_be_read_while_open()
    {
        var sampler = new StatsSampler(() => Task.FromResult(Snap(1, 0, 0)), () => throw new InvalidOperationException(), health: () => Task.FromResult<HealthReading?>(new HealthReading(4, 1, 0, false, 0, 0, 0)));
        var sample = await sampler.SampleAsync();
        using var log = new RunLog(_dir, ["setup line one", "line, with \"quotes\""], new DateTimeOffset(2026, 10, 29, 1, 2, 3, TimeSpan.Zero));
        log.Write(sample with { Note = "has, comma" }, new TraySnapshot(TrayState.Armed, "m", 500), 312.456);
        var text = await ReadSharedAsync(log.Path);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("# setup line one", lines[0]);
        Assert.Equal(string.Join(',', RunLog.Columns), lines[2].TrimEnd('\r'));
        var row = lines[3].TrimEnd('\r');
        Assert.Contains(",312.5,", row); Assert.Contains("Armed", row); Assert.Contains(",500,", row); Assert.EndsWith("\"has, comma\"", row);
        var header = lines[2].Split(',').Length;
        Assert.Equal(header, SplitCsv(row).Count);
    }

    [Fact]
    public void Run_logs_never_overwrite_an_existing_file()
    {
        var now = new DateTimeOffset(2026, 10, 29, 1, 2, 3, TimeSpan.Zero);
        using var first = new RunLog(_dir, [], now);
        Assert.Throws<IOException>(() => new RunLog(_dir, [], now));
    }

    [Fact]
    public void The_trays_data_folder_is_its_own()
    {
        Assert.EndsWith("SteamFrameTray", TrayData.Root);
        Assert.DoesNotContain("SteamLinkVRSettings", TrayData.SettingsPath + TrayData.SessionDirectory + TrayData.KnownHosts + TrayData.DiagnosticsDirectory + TrayData.LogsDirectory);
        Assert.True(TrayData.IsInside(TrayData.KnownHosts));
        Assert.False(TrayData.IsInside(Path.Combine(Path.GetDirectoryName(TrayData.Root)!, "SteamLinkVRSettings", "ssh-known-hosts")));
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static List<string> SplitCsv(string row)
    {
        var fields = new List<string>(); var current = new System.Text.StringBuilder(); var quoted = false;
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (quoted) { if (c == '"' && i + 1 < row.Length && row[i + 1] == '"') { current.Append('"'); i++; } else if (c == '"') quoted = false; else current.Append(c); }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
}
