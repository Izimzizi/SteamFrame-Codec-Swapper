using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamFrame.Core;

public sealed record CompositorTimingStats(int Samples, double MeanMs, double MedianMs, double P99Ms, double MaximumMs);
public sealed record CompositorTimingEvent(DateTimeOffset Timestamp, int RuntimePid, int ScenePid,
    long DroppedFrames, long ReprojectedFrames, long TimedOutEvents, long LateRenderFrames,
    long FrameHistoryGaps, double? AppGpuMaximumMs, double? TotalGpuMaximumMs, string Message)
{
    public long TimedOutPresentations { get; init; }
}
public sealed record CompositorTimingSnapshot(DateTimeOffset ObservedAt, bool Available, string Message,
    int? RuntimePid, int? ScenePid, long Presents, long DroppedFrames, long ReprojectedFrames,
    long TimedOutEvents, long LateRenderFrames, long FrameHistoryGaps, long SampledFrames,
    double? TargetFrameMs, CompositorTimingStats? AppGpu, CompositorTimingStats? TotalGpu,
    CompositorTimingStats? ClientInterval, CompositorTimingStats? Submit,
    string CapturePath, string? CaptureError, IReadOnlyList<CompositorTimingEvent> RecentEvents)
{
    public long TimedOutPresentations { get; init; }
}

/// <summary>
/// Public IVRCompositor_029 observer. Uses a background connection and never renders,
/// submits textures, changes settings/focus, or invokes native helper hooks.
/// Cumulative deltas describe PC presentation, not exact headset/network/decoder drops.
/// Counters start at a baseline and reset for each runtime identity/scene PID or rollback.
/// </summary>
public sealed class CompositorTiming
{
    public const int MaximumFramesPerPoll = 256;
    public const int MaximumRecentEvents = 128;
    public const int MaximumCaptureFileBytes = 1024 * 1024;
    private readonly SteamPaths _paths;
    private readonly object _gate = new();
    private readonly Func<NativeSample>? _testRead;
    private readonly Queue<CompositorTimingEvent> _recent = new();
    private NativeSample? _previous;
    private double _lastFrameTime;
    private uint _lastFrameIndex;
    private long _presents, _dropped, _reprojected, _timedOut, _timedOutPresentations, _late, _gaps, _sampled;
    private string? _captureError;
    private CompositorTimingSnapshot _current;
    public string CapturePath { get; }
    public CompositorTimingSnapshot Current { get { lock (_gate) return _current; } }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint GetTimings(nint frames, uint capacity);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetStats(ref CumulativeStats stats, uint size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint GetScenePid();

    public CompositorTiming(SteamPaths paths, string? diagnosticsDirectory = null)
        : this(paths, diagnosticsDirectory, null) { }

    internal CompositorTiming(SteamPaths paths, string? diagnosticsDirectory, Func<NativeSample>? testRead)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths)); _testRead = testRead;
        CapturePath = Path.Combine(Path.GetFullPath(diagnosticsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamFrameTray", "Diagnostics")), "compositor-timing.jsonl");
        ValidateLayout();
        _current = Snapshot(DateTimeOffset.Now, false, "Compositor timing has not been polled.", null, null, null, null, null, null, null);
    }

    public Task<CompositorTimingSnapshot> PollAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Poll(cancellationToken), cancellationToken);

    public CompositorTimingSnapshot Poll(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observed = DateTimeOffset.Now;
            NativeSample sample;
            try { sample = _testRead is null ? ReadNative() : _testRead(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                ResetSession();
                return _current = Snapshot(observed, false, "PC compositor timing unavailable; start SteamVR with a supported compositor interface.", null, null, null, null, null, null, null);
            }
            observed = DateTimeOffset.Now; // Timestamp the completed read batch, not its connection setup.
            if (!ValidateSample(sample))
            {
                ResetSession();
                return _current = Snapshot(observed, false, "PC compositor timing returned an unsupported layout or changing scene.", sample.RuntimePid, null, null, null, null, null, null);
            }
            if (sample.ScenePid == 0)
            {
                ResetSession();
                return _current = Snapshot(observed, true, "SteamVR is available; no active VR scene is presenting frames.", sample.RuntimePid, null, null, null, null, null, null);
            }
            var frames = sample.Frames.Where(f => double.IsFinite(f.SystemSeconds) && f.SystemSeconds > 0)
                .ToArray(); // OpenVR returns oldest first; predicted vsync times can move backwards.
            var baseline = _previous is null || _previous.RuntimePid != sample.RuntimePid ||
                _previous.RuntimeStartTicks != sample.RuntimeStartTicks || _previous.ScenePid != sample.ScenePid ||
                CountersRolledBack(_previous.Stats, sample.Stats);
            if (baseline)
            {
                _presents = _dropped = _reprojected = _timedOut = _timedOutPresentations = _late = _gaps = _sampled = 0;
                _lastFrameTime = frames.LastOrDefault().SystemSeconds;
                _lastFrameIndex = frames.LastOrDefault().Index;
                _previous = sample;
                return _current = Snapshot(observed, true, "PC compositor baseline started; existing presentation history was excluded.", sample.RuntimePid, (int)sample.ScenePid,
                    null, null, null, null, null);
            }
            var before = _previous!;
            long presents = sample.Stats.Presents - before.Stats.Presents;
            long dropped = sample.Stats.Dropped - before.Stats.Dropped;
            long reprojected = sample.Stats.Reprojected - before.Stats.Reprojected;
            long timedOut = sample.Stats.TimedOut - before.Stats.TimedOut;
            long timedOutPresentations = sample.Stats.TimedOutPresentations - before.Stats.TimedOutPresentations;
            long submits = sample.Stats.Submits - before.Stats.Submits;
            double? budget = submits > 0 ? (sample.Stats.SumTargetFrameTimes - before.Stats.SumTargetFrameTimes) * 1000 / submits : null;
            if (budget is not > 0 or > 1000 || !double.IsFinite(budget.Value)) budget = null;
            var added = new List<FrameTiming>(); long gaps = 0, late = 0;
            foreach (var frame in frames.OrderBy(f => unchecked(f.Index - _lastFrameIndex)))
            {
                var distance = unchecked(frame.Index - _lastFrameIndex);
                // Index is the frame identity. Clock corrections can give consecutive
                // frames equal/reversed times; time-based sorting invented two gaps.
                if (distance == 0 || distance >= 0x80000000u) continue;
                // This is missing sampled history only. Never turn index gaps into drops.
                if (_lastFrameTime > 0 && distance > 1 && distance < 100_000) gaps += distance - 1;
                _lastFrameTime = frame.SystemSeconds; _lastFrameIndex = frame.Index;
                added.Add(frame); if (frame.MisPresented > 0) ++late;
            }
            _presents += presents; _dropped += dropped; _reprojected += reprojected; _timedOut += timedOut;
            _timedOutPresentations += timedOutPresentations;
            _late += late; _gaps += gaps; _sampled += added.Count;
            var appGpu = Timing(added.Select(f => (double)f.AppPreGpuMs));
            var totalGpu = Timing(added.Select(f => (double)f.TotalRenderGpuMs));
            var client = Timing(added.Select(f => (double)f.ClientIntervalMs));
            var submit = Timing(added.Select(f => (double)f.SubmitMs));
            if (dropped + reprojected + timedOut + timedOutPresentations + late > 0)
            {
                var item = new CompositorTimingEvent(observed, sample.RuntimePid, (int)sample.ScenePid,
                    dropped, reprojected, timedOut, late, gaps, appGpu?.MaximumMs, totalGpu?.MaximumMs,
                    "PC compositor reported a presentation anomaly; receiver/network dropped-frame counts are separate.")
                    { TimedOutPresentations = timedOutPresentations };
                _recent.Enqueue(item); while (_recent.Count > MaximumRecentEvents) _recent.Dequeue();
                Persist(item);
            }
            _previous = sample;
            return _current = Snapshot(observed, true,
                FormattableString.Invariant($"PC compositor session: {_dropped} dropped presentations, {_reprojected} reprojected presentations, {_timedOut} timeout events ({_timedOutPresentations} timeout presentations) and {_late} frames presented on an unpredicted vsync. {_gaps} unobserved history entries are sampling gaps, not drops."),
                sample.RuntimePid, (int)sample.ScenePid, budget, appGpu, totalGpu, client, submit);
        }
    }

    private CompositorTimingSnapshot Snapshot(DateTimeOffset at, bool available, string message, int? runtime, int? scene,
        double? budget, CompositorTimingStats? app, CompositorTimingStats? total, CompositorTimingStats? client, CompositorTimingStats? submit)
        => new(at, available, message, runtime, scene, _presents, _dropped, _reprojected, _timedOut,
            _late, _gaps, _sampled, budget, app, total, client, submit, CapturePath, _captureError, Array.AsReadOnly(_recent.ToArray()))
            { TimedOutPresentations = _timedOutPresentations };

    private void ResetSession()
    {
        _previous = null; _lastFrameTime = 0; _lastFrameIndex = 0;
        _presents = _dropped = _reprojected = _timedOut = _timedOutPresentations = _late = _gaps = _sampled = 0;
    }

    private NativeSample ReadNative()
    {
        var runtime = RuntimeIdentity();
        var result = OpenVrSettings.WithInterface(_paths, "FnTable:IVRCompositor_029", table =>
        {
            var framesFunction = Marshal.GetDelegateForFunctionPointer<GetTimings>(Marshal.ReadIntPtr(table, 11 * IntPtr.Size));
            var statsFunction = Marshal.GetDelegateForFunctionPointer<GetStats>(Marshal.ReadIntPtr(table, 13 * IntPtr.Size));
            var sceneFunction = Marshal.GetDelegateForFunctionPointer<GetScenePid>(Marshal.ReadIntPtr(table, 24 * IntPtr.Size));
            uint scene = sceneFunction();
            var buffer = Marshal.AllocHGlobal(192 * MaximumFramesPerPoll);
            try
            {
                Marshal.WriteInt32(buffer, 192); // Only the first entry's size is required by Valve's API.
                var count = framesFunction(buffer, MaximumFramesPerPoll);
                if (count > MaximumFramesPerPoll) throw new IOException();
                var frames = new FrameTiming[count];
                for (int i = 0; i < count; i++) frames[i] = Marshal.PtrToStructure<FrameTiming>(buffer + i * 192);
                CumulativeStats stats = default;
                statsFunction(ref stats, 112);
                if (sceneFunction() != scene) throw new IOException();
                return new NativeSample(runtime.Pid, runtime.StartTicks, scene, stats, frames);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        });
        if (RuntimeIdentity() != runtime) throw new IOException();
        return result;
    }

    private static (int Pid, long StartTicks) RuntimeIdentity()
    {
        var processes = Process.GetProcessesByName("vrserver");
        try
        {
            if (processes.Length != 1 || processes[0].HasExited) throw new IOException();
            return (processes[0].Id, processes[0].StartTime.ToUniversalTime().Ticks);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static bool ValidateSample(NativeSample sample) => sample.RuntimePid > 0 && sample.RuntimeStartTicks > 0 && sample.ScenePid <= int.MaxValue &&
        (sample.ScenePid == 0 || sample.Stats.Pid == sample.ScenePid) && sample.Frames is not null && sample.Frames.Count <= MaximumFramesPerPoll &&
        sample.Frames.All(f => f.Size == 192) && double.IsFinite(sample.Stats.SumTargetFrameTimes) && sample.Stats.SumTargetFrameTimes >= 0;

    private static bool CountersRolledBack(CumulativeStats before, CumulativeStats after)
        => after.Presents < before.Presents || after.Dropped < before.Dropped || after.Reprojected < before.Reprojected ||
            after.TimedOut < before.TimedOut || after.TimedOutPresentations < before.TimedOutPresentations ||
            after.Submits < before.Submits || after.SumTargetFrameTimes < before.SumTargetFrameTimes;

    private static CompositorTimingStats? Timing(IEnumerable<double> values)
    {
        var sorted = values.Where(v => double.IsFinite(v) && v >= 0 && v <= 10000).Order().ToArray();
        if (sorted.Length == 0) return null;
        int n = sorted.Length; var median = n % 2 == 0 ? (sorted[n / 2 - 1] + sorted[n / 2]) / 2 : sorted[n / 2];
        return new(n, sorted.Average(), median, sorted[Math.Clamp((int)Math.Ceiling(n * .99) - 1, 0, n - 1)], sorted[^1]);
    }

    private void Persist(CompositorTimingEvent item)
    {
        _captureError = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CapturePath)!);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }) + "\n");
            if (File.Exists(CapturePath) && new FileInfo(CapturePath).Length + bytes.Length > MaximumCaptureFileBytes)
            {
                File.Delete(CapturePath + ".2");
                if (File.Exists(CapturePath + ".1")) File.Move(CapturePath + ".1", CapturePath + ".2");
                File.Move(CapturePath, CapturePath + ".1");
            }
            using var output = CaptureFile.OpenAppend(CapturePath);
            output.Write(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { _captureError = "Compositor diagnostic capture is unavailable."; }
    }

    internal static void ValidateLayout()
    {
        if (IntPtr.Size != 8 || Marshal.SizeOf<FrameTiming>() != 192 || Marshal.SizeOf<CumulativeStats>() != 112 ||
            Marshal.OffsetOf<FrameTiming>(nameof(FrameTiming.SystemSeconds)).ToInt32() != 24 ||
            Marshal.OffsetOf<FrameTiming>(nameof(FrameTiming.AppPreGpuMs)).ToInt32() != 32 ||
            Marshal.OffsetOf<FrameTiming>(nameof(FrameTiming.TransferLatencyMs)).ToInt32() != 184 ||
            Marshal.OffsetOf<CumulativeStats>(nameof(CumulativeStats.TimedOutPresentations)).ToInt32() != 48 ||
            Marshal.OffsetOf<CumulativeStats>(nameof(CumulativeStats.SumTargetFrameTimes)).ToInt32() != 80)
            throw new InvalidOperationException("Compositor timing requires the verified Windows x64 layout.");
    }

    internal sealed record NativeSample(int RuntimePid, long RuntimeStartTicks, uint ScenePid,
        CumulativeStats Stats, IReadOnlyList<FrameTiming> Frames);

    // Explicit offsets match Valve's Windows x64 openvr.h IVRCompositor_029.
    // Opaque pose bytes in the native timing structure have no managed fields and are never captured.
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    internal struct FrameTiming
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Index;
        [FieldOffset(8)] public uint Presents;
        [FieldOffset(12)] public uint MisPresented;
        [FieldOffset(16)] public uint Dropped;
        [FieldOffset(20)] public uint ReprojectionFlags;
        [FieldOffset(24)] public double SystemSeconds;
        [FieldOffset(32)] public float AppPreGpuMs;
        [FieldOffset(40)] public float TotalRenderGpuMs;
        [FieldOffset(56)] public float ClientIntervalMs;
        [FieldOffset(68)] public float SubmitMs;
        [FieldOffset(184)] public float TransferLatencyMs;
    }
    [StructLayout(LayoutKind.Explicit, Size = 112)]
    internal struct CumulativeStats
    {
        [FieldOffset(0)] public uint Pid;
        [FieldOffset(4)] public uint Presents;
        [FieldOffset(8)] public uint Dropped;
        [FieldOffset(12)] public uint Reprojected;
        [FieldOffset(44)] public uint TimedOut;
        [FieldOffset(48)] public uint TimedOutPresentations;
        [FieldOffset(60)] public uint Submits;
        [FieldOffset(80)] public double SumTargetFrameTimes;
    }
}
