using SteamFrame.Core;

namespace SteamFrameTray;

/// <summary>What the encoder was last configured with (as accepted by NVENC), when SteamVR's encoder reports it.</summary>
public sealed record EncoderConfigSample(double AverageMbps, double MaxMbps, string RateControl, uint VbvSize, string Preset, double? Fps);

/// <summary>Frames the link or the PC held back or dropped. Totals count from when sampling started.</summary>
public sealed record HealthReading(long HeldTotal, long HeldLastMinute, long LinkStalls, bool CompositorAvailable, long CompositorDropped, long CompositorLate, long CompositorReprojected);

/// <summary>One reading. A missing rate is a gap, never a zero or the requested target.</summary>
public sealed record StatsSample(DateTimeOffset At, double? Mbps, double? Fps, string Codec, bool CodecVerified, string CodecDetail,
    bool SteamVrRunning, ulong EncodeErrors, string? Note)
{
    public EncoderConfigSample? Config { get; init; }
    public HealthReading? Health { get; init; }
    public ulong EncodedFrames { get; init; }
    public ulong EncodedBytes { get; init; }
    /// <summary>Mean whole-frame quantizer over the last interval, when the encoder reports it.</summary>
    public double? AverageQp { get; init; }
    public uint? LastFrameQp { get; init; }
    public uint? LastFrameBytes { get; init; }
}

/// <summary>
/// Turns encoder counters into live Mbps/FPS and a verified codec name. UI-free so a window now, and
/// an in-VR overlay later, can share it.
/// </summary>
public sealed class StatsSampler
{
    public const int HistoryLength = 60;
    private static readonly TimeSpan MonitorRetry = TimeSpan.FromSeconds(15);
    private readonly Func<Task<EncoderSnapshot>> _read;
    private readonly Func<Task<EncoderSnapshot>> _startMonitor;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Task<HealthReading?>>? _health;
    private readonly RateTracker _rate = new();
    private readonly List<double?> _history = [];
    private int _monitorPid;
    private DateTimeOffset _nextMonitorAttempt;
    private string? _monitorNote;
    private (string Generation, ulong Sum, ulong Samples)? _qp;

    public StatsSampler(Func<Task<EncoderSnapshot>> read, Func<Task<EncoderSnapshot>> startMonitor, Func<DateTimeOffset>? now = null, Func<Task<HealthReading?>>? health = null)
    {
        _read = read; _startMonitor = startMonitor; _now = now ?? (() => DateTimeOffset.UtcNow); _health = health;
    }

    /// <summary>The most recent Mbps readings, oldest first; null entries are gaps.</summary>
    public IReadOnlyList<double?> History => _history;

    /// <summary>Mean of the readings in the history window (the last 60 samples, about 60 s); gaps are skipped. Null when there are none.</summary>
    public double? RecentAverage
    {
        get
        {
            double sum = 0; var count = 0;
            foreach (var value in _history) if (value is double v) { sum += v; count++; }
            return count == 0 ? null : sum / count;
        }
    }

    public async Task<StatsSample> SampleAsync()
    {
        var snapshot = await ReadAsync();
        var codec = StreamCodecStatus.From(snapshot);
        double? mbps = null, fps = null;
        if (snapshot.Active && snapshot.Generation.Length > 0)
            (mbps, fps) = _rate.Sample(snapshot.Generation, snapshot.EncodedBytes, snapshot.EncodedFrames, snapshot.TimestampSeconds);
        else _rate.Reset();
        _history.Add(mbps);
        if (_history.Count > HistoryLength) _history.RemoveAt(0);
        var note = snapshot.ProcessId == 0 ? null : _monitorNote ?? (codec.Verified ? null : snapshot.Message);
        HealthReading? health = null;
        if (_health is not null)
        {
            try { health = await _health(); }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException) { }
        }
        return new(_now(), mbps, fps, codec.Value, codec.Verified, codec.Detail, snapshot.ProcessId != 0, snapshot.EncodeErrors, note)
        {
            Config = ConfigOf(snapshot), Health = health, EncodedFrames = snapshot.EncodedFrames, EncodedBytes = snapshot.EncodedBytes,
            AverageQp = QpOf(snapshot), LastFrameQp = snapshot.LastFrameQp, LastFrameBytes = snapshot.LastFrameBytes,
        };
    }

    private static EncoderConfigSample? ConfigOf(EncoderSnapshot snapshot)
    {
        if (snapshot.EncodingPolicy is not { Observed: true } policy || policy.AverageBitrate == 0) return null;
        double? fps = policy.FrameRateDen == 0 ? null : (double)policy.FrameRateNum / policy.FrameRateDen;
        return new(policy.AverageBitrate / 1e6, policy.MaxBitrate / 1e6, policy.RateControlName, policy.VbvSize, policy.Preset, fps);
    }

    private double? QpOf(EncoderSnapshot snapshot)
    {
        if (snapshot.Generation.Length == 0 || snapshot.QuantizerSamples == 0) { _qp = null; return null; }
        var current = (snapshot.Generation, snapshot.QuantizerSum, snapshot.QuantizerSamples);
        var previous = _qp;
        _qp = current;
        if (previous is not { } before || before.Generation != current.Generation || current.QuantizerSamples <= before.Samples || current.QuantizerSum < before.Sum) return null;
        return (double)(current.QuantizerSum - before.Sum) / (current.QuantizerSamples - before.Samples);
    }

    private async Task<EncoderSnapshot> ReadAsync()
    {
        EncoderSnapshot snapshot;
        try { snapshot = await _read(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new() { CodecReadFailed = true, Message = "Encoder status could not be read: " + error.Message };
        }
        if (snapshot.ProcessId == 0) { _monitorPid = 0; _monitorNote = null; return snapshot; }
        // The paired H.264 backend reports on its own; the stock HEVC path needs the monitor started once per SteamVR run.
        if (snapshot.Installed || snapshot.PairedCodecTrialPresent || snapshot.WaitingForDriver || !snapshot.Supported && !string.IsNullOrEmpty(snapshot.Message) && snapshot.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase))
        {
            if (snapshot.Installed) _monitorNote = null;
            return snapshot;
        }
        var now = _now();
        if (_monitorPid == snapshot.ProcessId && now < _nextMonitorAttempt) return snapshot;
        _monitorPid = snapshot.ProcessId; _nextMonitorAttempt = now + MonitorRetry;
        try { var started = await _startMonitor(); _monitorNote = null; return started; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _monitorNote = "Could not start HEVC monitoring: " + error.Message + (error is UnauthorizedAccessException or System.ComponentModel.Win32Exception ? " If SteamVR runs as administrator, run this app as administrator too." : "");
            return snapshot;
        }
    }
}
