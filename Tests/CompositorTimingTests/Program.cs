using System.Runtime.InteropServices;
using System.Text.Json;
using SteamFrame.Core;

if (args.Length == 3 && args[0] == "--live")
{
    var directory = Path.GetFullPath(args[1]); var seconds = int.Parse(args[2]);
    if (seconds < 1 || seconds > 60) throw new ArgumentException("Live observation must be bounded to 1–60 seconds.");
    Directory.CreateDirectory(directory);
    var observer = new CompositorTiming(SteamPaths.Discover(), directory);
    for (int i = 0; i <= seconds; i++)
    {
        var snapshot = await observer.PollAsync();
        var json = JsonSerializer.Serialize(snapshot);
        File.AppendAllText(Path.Combine(directory, "managed-timing.jsonl"), json + "\n");
        Console.WriteLine(json);
        if (i < seconds) await Task.Delay(1000);
    }
    return;
}

var root = Path.Combine(Path.GetTempPath(), "SteamLinkVRSettings-compositor-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int assertions = 0;
void Check(bool condition, string description)
{
    ++assertions; if (!condition) throw new InvalidOperationException(description);
}
CompositorTiming.FrameTiming Frame(uint index, double at, float gpu = 2, uint mis = 0, uint flags = 4)
    => new() { Size = 192, Index = index, SystemSeconds = at, Presents = 1, MisPresented = mis,
        AppPreGpuMs = gpu, TotalRenderGpuMs = gpu + .25f, ClientIntervalMs = 8.333f, SubmitMs = .05f, ReprojectionFlags = flags };
CompositorTiming.NativeSample Sample(uint presents, uint dropped = 0, uint repro = 0, uint timeout = 0,
    int runtime = 100, long started = 10000, uint scene = 200, params CompositorTiming.FrameTiming[] frames)
    => new(runtime, started, scene, new() { Pid = scene, Presents = presents, Dropped = dropped,
        Reprojected = repro, TimedOut = timeout, Submits = presents, SumTargetFrameTimes = presents / 120d }, frames);
var paths = new SteamPaths(root, root);
try
{
    CompositorTiming.ValidateLayout();
    Check(Marshal.SizeOf<CompositorTiming.FrameTiming>() == 192 && Marshal.SizeOf<CompositorTiming.CumulativeStats>() == 112,
        "Windows x64 public timing layouts must have their independently verified sizes.");
    var current = Sample(1000, dropped: 12, repro: 20, timeout: 2, frames: [Frame(1000, 10)]);
    var collector = new CompositorTiming(paths, Path.Combine(root, "main"), () => current);
    var state = collector.Poll();
    Check(state.Available && state.DroppedFrames == 0 && state.Presents == 0 && state.SampledFrames == 0 && !File.Exists(state.CapturePath),
        "First observation must exclude pre-existing cumulative drops and frame history.");
    current = Sample(1002, dropped: 14, repro: 21, timeout: 3,
        frames: [Frame(1000, 10), Frame(1001, 10.008333, 3), Frame(1002, 10.016666, 5, mis: 1)]);
    state = collector.Poll();
    Check(state.Presents == 2 && state.DroppedFrames == 2 && state.ReprojectedFrames == 1 && state.TimedOutEvents == 1 && state.LateRenderFrames == 1,
        "Only independently reported cumulative counter increments may become presentation drop/reprojection/timeout counts.");
    Check(state.SampledFrames == 2 && state.FrameHistoryGaps == 0 && state.AppGpu?.Samples == 2 && state.AppGpu.MedianMs == 4 && state.AppGpu.P99Ms == 5,
        "GPU statistics must use only newly observed frames, not overlapping history.");
    Check(state.TargetFrameMs is double budget && Math.Abs(budget - 1000d / 120d) < .001,
        "Runtime target frame sums are seconds and must convert to the correct 120 Hz budget in milliseconds.");
    Check(state.RecentEvents.Single().DroppedFrames == 2 && state.RecentEvents.Single().LateRenderFrames == 1,
        "A capture record must contain this poll's anomaly deltas, not cumulative historical counts.");
    var capture = File.ReadAllText(state.CapturePath);
    Check(!capture.Contains("Pose") && !capture.Contains("password") && !capture.Contains(paths.SteamRoot) && capture.Contains("droppedFrames"),
        "Capture must contain numeric evidence and fixed synthesized text, not poses, exceptions or paths.");
    state = collector.Poll();
    Check(state.Presents == 2 && state.SampledFrames == 2 && state.AppGpu == null && state.RecentEvents.Count == 1,
        "Unchanged history must neither replay frames nor create a second anomaly event.");

    current = Sample(1010, dropped: 14, repro: 21, timeout: 3,
        frames: [Frame(1009, 10.075, flags: 4), Frame(1010, 10.083333, flags: 4)]);
    state = collector.Poll();
    Check(state.FrameHistoryGaps == 6 && state.DroppedFrames == 2 && state.ReprojectedFrames == 1,
        "Lost sampled history and async mode flags must not invent compositor drop/reprojection increments.");
    current = Sample(1011, dropped: 14, repro: 21, timeout: 3,
        frames: [Frame(1010, 10.083333), Frame(1011, 10.091666), Frame(1011, 10.091666)]);
    state = collector.Poll();
    Check(state.SampledFrames == 5 && state.FrameHistoryGaps == 6,
        "Duplicate and overlapping frame entries must be deduplicated by their system time.");

    var correctedSample = Sample(1, frames: [Frame(100, 10)]);
    var corrected = new CompositorTiming(paths, Path.Combine(root, "clock-correction"), () => correctedSample); corrected.Poll();
    correctedSample = Sample(4, frames: [Frame(100, 10), Frame(101, 10.011111), Frame(102, 10.0111104), Frame(103, 10.033333)]);
    var correctedState = corrected.Poll();
    Check(correctedState.FrameHistoryGaps == 0 && correctedState.SampledFrames == 3,
        "Reversed predicted vsync timestamps must not invent missing history or discard a real frame.");
    corrected.Poll();
    Check(corrected.Current.SampledFrames == 3, "Overlapping corrected timestamps must not replay frame identities.");

    var timeoutSample=Sample(1,frames:[Frame(1,1)]);
    var timeoutObserver=new CompositorTiming(paths,Path.Combine(root,"timeout-presentations"),()=>timeoutSample);timeoutObserver.Poll();
    timeoutSample=Sample(2,timeout:1,frames:[Frame(2,1.011)]);
    var timeoutState=timeoutObserver.Poll();
    Check(timeoutState.TimedOutEvents==1 && timeoutState.TimedOutPresentations==0,
        "Entering timeout state must not invent a presentation in that state.");
    var timeoutStats=timeoutSample.Stats;timeoutStats.Presents=5;timeoutStats.TimedOutPresentations=3;
    timeoutSample=timeoutSample with {Stats=timeoutStats,Frames=[Frame(3,1.022),Frame(4,1.033),Frame(5,1.044)]};
    timeoutState=timeoutObserver.Poll();
    Check(timeoutState.TimedOutEvents==1 && timeoutState.TimedOutPresentations==3 && timeoutState.RecentEvents.Last().TimedOutPresentations==3,
        "Timeout presentations can advance without entering a new timeout state.");
    timeoutStats.TimedOutPresentations=0;timeoutSample=timeoutSample with {Stats=timeoutStats};
    Check(timeoutObserver.Poll().TimedOutPresentations==0,"Timeout presentation counter rollback resets its baseline.");

    current = Sample(20, scene: 300, frames: [Frame(20, 11)]);
    state = collector.Poll();
    Check(state.ScenePid == 300 && state.DroppedFrames == 0 && state.SampledFrames == 0,
        "A different scene PID must establish a new baseline rather than subtract across applications.");
    current = Sample(21, dropped: 1, scene: 300, frames: [Frame(21, 11.008333)]); collector.Poll();
    current = Sample(21, dropped: 1, runtime: 101, scene: 300, frames: [Frame(21, 11.008333)]);
    state = collector.Poll();
    Check(state.RuntimePid == 101 && state.DroppedFrames == 0, "A different runtime PID must reset session deltas.");
    current = Sample(22, dropped: 2, runtime: 101, scene: 300, frames: [Frame(22, 11.016666)]); collector.Poll();
    current = Sample(22, dropped: 2, runtime: 101, started: 20000, scene: 300, frames: [Frame(22, 11.016666)]);
    state = collector.Poll();
    Check(state.DroppedFrames == 0, "Reused runtime PID with a new process start time must not share a baseline.");
    current = Sample(30, dropped: 4, runtime: 101, started: 20000, scene: 300, frames: [Frame(30, 11.1)]); collector.Poll();
    current = Sample(2, dropped: 0, runtime: 101, started: 20000, scene: 300, frames: [Frame(2, 11.2)]);
    state = collector.Poll();
    Check(state.DroppedFrames == 0 && state.Presents == 0 && state.FrameHistoryGaps == 0,
        "Counter rollback must establish a new baseline without unsigned underflow or false drop totals.");

    var wrapSample = Sample(1, frames: [Frame(uint.MaxValue, 1)]);
    var wrap = new CompositorTiming(paths, Path.Combine(root, "wrap"), () => wrapSample); wrap.Poll();
    wrapSample = Sample(2, frames: [Frame(0, 1.008333)]);
    Check(wrap.Poll().FrameHistoryGaps == 0 && wrap.Current.SampledFrames == 1, "Natural uint frame-index wrap must not create a huge sampling gap.");

    var noFinite = Sample(1, frames: [Frame(1, 1)]);
    var finite = new CompositorTiming(paths, Path.Combine(root, "finite"), () => noFinite); finite.Poll();
    noFinite = Sample(3, frames: [Frame(2, 1.008333, float.NaN), Frame(3, 1.016666, -1)]);
    Check(finite.Poll().AppGpu == null, "Invalid or negative GPU durations must be excluded from distributions.");
    var wrong = Frame(4, 1.025); wrong.Size = 188;
    noFinite = Sample(4, frames: [wrong]);
    Check(!finite.Poll().Available, "An unsupported returned timing structure must fail closed.");
    noFinite = Sample(4, frames: [Frame(4, 1.025)]); finite.Poll();
    noFinite = Sample(5, frames: [Frame(5, 1.033333)]);
    Check(finite.Poll().Presents == 1, "Recovery after invalid timing must exclude unobserved historical deltas.");
    noFinite = Sample(6, frames: [Frame(6, 1.041666)]);
    var mismatched = noFinite.Stats; mismatched.Pid = 201;
    noFinite = noFinite with { Stats = mismatched };
    Check(!finite.Poll().Available && finite.Current.ScenePid == null,
        "Cumulative counters from a previously connected application must not be attributed to the current scene.");
    noFinite = Sample(6, frames: Enumerable.Range(0, 257).Select(i => Frame((uint)i, i / 120d + 1)).ToArray());
    Check(!finite.Poll().Available, "A response beyond the bounded frame capacity must fail closed.");
    noFinite = Sample(6, scene: 0, frames: []);
    Check(finite.Poll().Available && finite.Current.ScenePid == null && finite.Current.AppGpu == null && finite.Current.Presents == 0,
        "A runtime without a scene must report no active renderer and no current GPU timing.");

    var failure = new CompositorTiming(paths, Path.Combine(root, "failure"), () => throw new IOException("secret credentials in exception"));
    state = failure.Poll();
    Check(!state.Available && !state.Message.Contains("secret") && !File.Exists(state.CapturePath),
        "Runtime failure must expose a fixed safe message and never persist exception text.");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var before = collector.Current;
    try { collector.Poll(cancelled.Token); throw new InvalidOperationException("Cancellation ignored."); }
    catch (OperationCanceledException) { }
    Check(ReferenceEquals(before, collector.Current), "Cancellation before sampling must preserve the existing snapshot.");

    var blocked = Path.Combine(root, "blocked-capture"); File.WriteAllText(blocked, "fixture");
    var blockedSample = Sample(1, frames: [Frame(1, 1)]);
    var blockedCapture = new CompositorTiming(paths, blocked, () => blockedSample); blockedCapture.Poll();
    blockedSample = Sample(2, dropped: 1, frames: [Frame(2, 1.008333)]);
    state = blockedCapture.Poll();
    Check(state.DroppedFrames == 1 && state.CaptureError == "Compositor diagnostic capture is unavailable.",
        "Capture failure must not erase a current compositor drop or expose path/exception text.");

    var burstSample = Sample(1, frames: [Frame(1, 1)]);
    var burst = new CompositorTiming(paths, Path.Combine(root, "bounded"), () => burstSample); burst.Poll();
    Directory.CreateDirectory(Path.GetDirectoryName(burst.CapturePath)!);
    File.WriteAllText(burst.CapturePath, new string(' ', CompositorTiming.MaximumCaptureFileBytes));
    for (uint i = 2; i <= 150; i++)
    {
        burstSample = Sample(i, dropped: i, frames: [Frame(i, i / 120d + 1)]); burst.Poll();
    }
    Check(burst.Current.RecentEvents.Count == CompositorTiming.MaximumRecentEvents && File.Exists(burst.CapturePath + ".1") &&
        new FileInfo(burst.CapturePath).Length < CompositorTiming.MaximumCaptureFileBytes,
        "Recent anomaly records and persistent capture files must stay bounded during repeated drops.");
    Console.WriteLine($"Compositor timing: {assertions} assertions passed.");
}
finally
{
    var resolved = Path.GetFullPath(root);
    var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("SteamLinkVRSettings-compositor-tests-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unsafe fixture cleanup path.");
    Directory.Delete(resolved, recursive: true);
}
