using System.Globalization;
using System.Text;
using System.Text.Json;
using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>What a benchmark steps through. The codec and encoder settings are whatever is selected when it starts.</summary>
public sealed record BenchmarkSettings(int StartMbps, int CeilingMbps, int StepMbps, TimeSpan Settle, TimeSpan Measure, bool StopAfterFailure = true, bool UseScene = false)
{
    public static BenchmarkSettings Default(bool driverPatched) => new(100, driverPatched ? WirelessAdvisor.PatchedCeilingMbps : WirelessAdvisor.StockCeilingMbps, 50, LinkCapacitySweep.SettleTime, LinkCapacitySweep.MeasureTime);

    public IReadOnlyList<int> Targets => LinkCapacitySweep.Plan(StartMbps, CeilingMbps, StepMbps);

    /// <summary>How long the run takes at the longest (re-checks of unclean steps are extra).</summary>
    public TimeSpan Estimate => (Settle + Measure) * Targets.Count + LinkCapacitySweep.QuietTime;
}

/// <summary>One measured bitrate target. Counters are what happened during the measuring window only.</summary>
public sealed record BenchmarkStep(
    int TargetMbps, double Seconds, double? MeanMbps, double? MeanFps, long HeldFrames, double HeldMs, long PcDropped, long PcLate, long PcReprojected,
    double? EncodeAverageMs, double? EncodeMaxMs, double? AverageQp, long StreamResets, long EncodeErrors, SweepVerdict Verdict, string Reason, int Passes)
{
    /// <summary>What the encoder was actually asked for (average and peak) during the step, as seen by the encoder monitor; null when it was not observed.</summary>
    public double? AskedMbps { get; init; }
    public double? AskedMaxMbps { get; init; }
    /// <summary>Stream recovery requests in each second of the window (comma separated), kept when there were any, so a failed window shows when the trouble started.</summary>
    public string? ResetsPerSecond { get; init; }
    public double HeldPerMinute => Seconds > 0 ? HeldFrames * 60.0 / Seconds : 0;
    public double HeldMsPerMinute => Seconds > 0 ? HeldMs * 60.0 / Seconds : 0;
}

/// <summary>A finished (or stopped) benchmark: the setup, every step, and the conclusion.</summary>
public sealed class BenchmarkRun
{
    public DateTimeOffset At { get; set; }
    public string Codec { get; set; } = "";
    /// <summary>The setup being measured, one fact per line (rate control, preset, width, refresh...).</summary>
    public List<string> Setup { get; set; } = [];
    public int StartMbps { get; set; }
    public int CeilingMbps { get; set; }
    public int StepMbps { get; set; }
    public int MeasureSeconds { get; set; }
    public List<BenchmarkStep> Steps { get; set; } = [];
    public string Conclusion { get; set; } = "";
    public int? BestCleanMbps { get; set; }
    public bool Stopped { get; set; }
    public string? Error { get; set; }

    public string Title => $"{At.LocalDateTime:MMM d HH:mm} · {Codec}" + (BestCleanMbps is int best ? $" · clean to {best} Mbps" : " · no clean step") + (Stopped ? " (stopped)" : "");
}

public sealed record BenchmarkProgress(int StepIndex, int StepCount, int TargetMbps, string Phase, double PhaseSecondsLeft, BenchmarkStep? Finished);

/// <summary>
/// Steps the bitrate target upward while a stream runs, measures each step from the one-second samples (held frames,
/// PC drops, encode time, delivered rate) and judges it with <see cref="LinkCapacitySweep"/>. It changes nothing but the target
/// it is told to apply; the caller restores the user's own target afterwards.
/// </summary>
public sealed class BenchmarkRunner
{
    private readonly Func<int, CancellationToken, Task> _apply;
    private readonly Func<CancellationToken, Task<StatsSample>> _next;

    /// <param name="apply">Sets the bitrate target live.</param>
    /// <param name="next">Waits for the next one-second sample.</param>
    public BenchmarkRunner(Func<int, CancellationToken, Task> apply, Func<CancellationToken, Task<StatsSample>> next)
    {
        _apply = apply; _next = next;
    }

    public async Task<BenchmarkRun> RunAsync(BenchmarkSettings settings, BenchmarkRun run, IProgress<BenchmarkProgress>? progress, CancellationToken token)
    {
        run.StartMbps = settings.StartMbps; run.CeilingMbps = settings.CeilingMbps; run.StepMbps = settings.StepMbps; run.MeasureSeconds = (int)settings.Measure.TotalSeconds;
        var targets = settings.Targets;
        var results = new List<SweepStepResult>();
        try
        {
            run.Codec = await PrepareAsync(targets, progress, token);
            var rechecks = 0;
            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                await _apply(target, token);
                var started = await Settle(target, index, targets.Count, settings.Settle, progress, token);
                var result = await Measure(target, index, targets.Count, settings.Measure, "measuring", progress, token);
                if (LinkCapacitySweep.NeedsRecheck(result.Result, rechecks))
                {
                    rechecks++;
                    var again = await Measure(target, index, targets.Count, settings.Measure, "re-checking", progress, token);
                    result = Combine(result, again);
                }
                results.Add(result.Result);
                run.Steps.Add(result.Step);
                progress?.Report(new(index, targets.Count, target, "done", 0, result.Step));
                if (settings.StopAfterFailure && LinkCapacitySweep.ShouldStop(results)) break;
            }
        }
        catch (OperationCanceledException) { run.Stopped = true; }
        catch (InvalidOperationException error) { run.Error = error.Message; }
        var conclusion = LinkCapacitySweep.Conclude(results);
        run.BestCleanMbps = conclusion.BestCleanMbps;
        run.Conclusion = results.Count == 0 ? (run.Error ?? "Nothing was measured.") : conclusion.Summary + (run.Stopped ? " The run was stopped early." : "") + (run.Error is null ? "" : " " + run.Error);
        return run;
    }

    /// <summary>A window with at least this many stream recovery requests per second, after <see cref="StormSeconds"/> seconds, is cut short.</summary>
    public const double StormResetsPerSecond = 3, StormSeconds = 3;

    /// <summary>Waits until the stream has asked for no recovery for <paramref name="quiet"/>; false when it has not calmed down within <paramref name="limit"/>.</summary>
    public async Task<bool> WaitQuietAsync(TimeSpan quiet, TimeSpan limit, Action<double>? secondsLeft, CancellationToken token)
    {
        var first = await Next(token);
        var begun = first.At;
        var lastResets = first.Health?.StreamResets ?? 0;
        var quietSince = first.At;
        while (true)
        {
            var sample = await Next(token);
            var resets = sample.Health?.StreamResets ?? 0;
            if (resets != lastResets) { lastResets = resets; quietSince = sample.At; }
            secondsLeft?.Invoke(Math.Max(0, (quiet - (sample.At - quietSince)).TotalSeconds));
            if (sample.At - quietSince >= quiet) return true;
            if (sample.At - begun >= limit) return false;
        }
    }

    /// <summary>Recovery requests in one window that do not fail it (0 for bitrate steps; the encoder sweep allows the one or two a change of options causes).</summary>
    public int TolerableResets { get; set; }

    public Task ApplyTargetAsync(int mbps, CancellationToken token) => _apply(mbps, token);

    /// <summary>Checks that a stream is running, waits for it to be quiet and for encoded video to appear; returns the codec name.</summary>
    public async Task<string> PrepareAsync(IReadOnlyList<int> targets, IProgress<BenchmarkProgress>? progress, CancellationToken token)
    {
        var first = await _next(token);
        if (!first.SteamVrRunning) throw new InvalidOperationException("SteamVR is not running. Start SteamVR and the stream, put a busy scene on the headset, then run the benchmark.");
        var quiet = await WaitUntilQuietAsync(first, progress, targets, token);
        // The encoder reads as idle for a moment around a scene change or stream reset; give the video up to 30 s to show up before giving up.
        for (var waited = 0; quiet.Mbps is null && waited < 30; waited++) quiet = await Next(token);
        if (quiet.Mbps is null) throw new InvalidOperationException("No encoded video was seen for 30 s (" + (quiet.Note ?? (quiet.Fps is null ? "the encoder produced no frames" : "no bitrate was measured")) + "). Is the headset on and connected, and showing the scene?");
        return first.Codec;
    }

    /// <summary>One settle and measuring window at the current settings (judged as a step of <paramref name="target"/> Mbps); an unclean window is measured once more.</summary>
    public async Task<BenchmarkStep> MeasureStepAsync(int target, int index, int count, TimeSpan settle, TimeSpan window, bool recheck, IProgress<BenchmarkProgress>? progress, CancellationToken token, bool waitQuiet = false)
    {
        await Settle(target, index, count, settle, progress, token);
        // A change of encoder options restarts the encoder's rate control and sends a large new picture, which on a link near its limit
        // starts a short recovery storm; measuring that would measure the change, not the setting.
        if (waitQuiet) await WaitQuietAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(25), null, token);
        var result = await Measure(target, index, count, window, "measuring", progress, token);
        if (recheck && LinkCapacitySweep.NeedsRecheck(result.Result, 0))
            result = Combine(result, await Measure(target, index, count, window, "re-checking", progress, token));
        return result.Step;
    }

    private async Task<StatsSample> Next(CancellationToken token)
    {
        var sample = await _next(token);
        if (!sample.SteamVrRunning) throw new InvalidOperationException("SteamVR closed during the benchmark.");
        return sample;
    }

    /// <summary>A stream that has just started or switched scene asks for resets for a while; wait for quiet so that is not scored as the link failing.</summary>
    private async Task<StatsSample> WaitUntilQuietAsync(StatsSample first, IProgress<BenchmarkProgress>? progress, IReadOnlyList<int> targets, CancellationToken token)
    {
        var begun = first.At;
        var lastResets = first.Health?.StreamResets ?? 0;
        var quietSince = first.At;
        while (true)
        {
            var sample = await Next(token);
            var resets = sample.Health?.StreamResets ?? 0;
            if (resets != lastResets) { lastResets = resets; quietSince = sample.At; }
            progress?.Report(new(0, targets.Count, targets[0], "waiting for a quiet stream", Math.Max(0, (LinkCapacitySweep.QuietTime - (sample.At - quietSince)).TotalSeconds), null));
            if (sample.At - quietSince >= LinkCapacitySweep.QuietTime || sample.At - begun >= LinkCapacitySweep.QuietLimit) return sample;
        }
    }

    private async Task<DateTimeOffset> Settle(int target, int index, int count, TimeSpan settle, IProgress<BenchmarkProgress>? progress, CancellationToken token)
    {
        DateTimeOffset? began = null;
        while (true)
        {
            var sample = await Next(token);
            began ??= sample.At;
            var left = settle - (sample.At - began.Value);
            progress?.Report(new(index, count, target, "settling", Math.Max(0, left.TotalSeconds), null));
            if (left <= TimeSpan.Zero) return sample.At;
        }
    }

    private sealed record Measured(SweepStepResult Result, BenchmarkStep Step);

    private async Task<Measured> Measure(int target, int index, int count, TimeSpan window, string phase, IProgress<BenchmarkProgress>? progress, CancellationToken token)
    {
        var meter = new StepMeter();
        while (true)
        {
            var sample = await Next(token);
            meter.Add(sample);
            var left = window - meter.Elapsed;
            progress?.Report(new(index, count, target, phase, Math.Max(0, left.TotalSeconds), null));
            if (left <= TimeSpan.Zero) break;
            // A recovery storm (the receiver asking for a new picture several times a second) only gets worse the longer it is measured, and each new picture is
            // a large frame that adds to the load: the window ends early and counts as failed.
            if (meter.Elapsed.TotalSeconds >= StormSeconds && meter.ResetsSoFar >= StormResetsPerSecond * meter.Elapsed.TotalSeconds) break;
        }
        return meter.Finish(target, TolerableResets);
    }

    private static Measured Combine(Measured first, Measured second)
    {
        var sweep = LinkCapacitySweep.Recheck(first.Result, second.Result);
        // The numbers shown are those of the worse of the two passes (what Recheck keeps); the verdict and reason are the combined ones.
        var source = ReferenceEquals(sweep.Sample, first.Result.Sample) ? first.Step : second.Step;
        return new(sweep, source with { Verdict = sweep.Verdict, Reason = sweep.Reason, Passes = sweep.Passes });
    }

    /// <summary>Collects the samples of one measuring window.</summary>
    private sealed class StepMeter
    {
        private StatsSample? _first, _last;
        private readonly List<double> _mbps = [], _fps = [], _qp = [], _encodeAverage = [], _asked = [], _askedMax = [];
        private readonly List<long> _resetSeconds = [];
        private StatsSample? _previous;
        private double _encodeMax;
        private bool _timed;

        public TimeSpan Elapsed => _first is null || _last is null ? TimeSpan.Zero : _last.At - _first.At;

        /// <summary>Stream recovery requests since the window began.</summary>
        public long ResetsSoFar => _first?.Health is { } from && _last?.Health is { } to ? Delta(from.StreamResets, to.StreamResets) : 0;

        public void Add(StatsSample sample)
        {
            _first ??= sample; _last = sample;
            if (_previous?.Health is { } before && sample.Health is { } after) _resetSeconds.Add(Delta(before.StreamResets, after.StreamResets));
            _previous = sample;
            if (sample.Mbps is double m) _mbps.Add(m);
            if (sample.Fps is double f) _fps.Add(f);
            if (sample.AverageQp is double q) _qp.Add(q);
            if (sample.Config is { } config) { _asked.Add(config.AverageMbps); _askedMax.Add(config.MaxMbps); }
            if (sample.Timing is { } t) { _encodeAverage.Add(t.AverageMs); _encodeMax = Math.Max(_encodeMax, t.MaxMs); _timed = true; }
        }

        private static long Delta(long start, long end) => end >= start ? end - start : end;   // a counter that restarted (the user reset the counters) starts again from zero

        public Measured Finish(int target, int tolerableResets = 0)
        {
            var seconds = Elapsed.TotalSeconds;
            var from = _first?.Health; var to = _last?.Health;
            long Held() => from is null || to is null ? 0 : Delta(from.HeldTotal, to.HeldTotal);
            double HeldMs() => from is null || to is null ? 0 : to.HeldMsTotal >= from.HeldMsTotal ? to.HeldMsTotal - from.HeldMsTotal : to.HeldMsTotal;
            long Resets() => from is null || to is null ? 0 : Delta(from.StreamResets, to.StreamResets);
            long Dropped() => from is { CompositorAvailable: true } && to is { CompositorAvailable: true } ? Delta(from.CompositorDropped, to.CompositorDropped) : 0;
            long Late() => from is { CompositorAvailable: true } && to is { CompositorAvailable: true } ? Delta(from.CompositorLate, to.CompositorLate) : 0;
            long Reprojected() => from is { CompositorAvailable: true } && to is { CompositorAvailable: true } ? Delta(from.CompositorReprojected, to.CompositorReprojected) : 0;
            double? mean(List<double> values) => values.Count == 0 ? null : values.Average();
            var errors = _first is null || _last is null ? 0 : (long)(_last.EncodeErrors >= _first.EncodeErrors ? _last.EncodeErrors - _first.EncodeErrors : _last.EncodeErrors);
            var sample = new SweepSample(seconds, Held(), HeldMs(), 0, 0, null, null, null, Dropped(), mean(_mbps), mean(_fps), 0, Resets());
            var result = LinkCapacitySweep.Evaluate(target, sample, tolerableResets: tolerableResets);
            if (_mbps.Count == 0) result = result with { Verdict = SweepVerdict.Failed, Reason = "No encoded video was seen during the step." };
            if (errors > 0 && result.Verdict != SweepVerdict.Failed) result = result with { Verdict = SweepVerdict.Failed, Reason = $"{errors} encoder error{(errors == 1 ? "" : "s")}. " + result.Reason };
            var perSecond = _resetSeconds.Any(v => v > 0) ? string.Join(',', _resetSeconds) : null;
            if (perSecond is not null && result.Verdict != SweepVerdict.Clean)
                result = result with { Reason = result.Reason + " Recovery requests per second: " + (_resetSeconds.Count > 40 ? "… " + string.Join(',', _resetSeconds.Skip(_resetSeconds.Count - 40)) : perSecond) + "." };
            var step = new BenchmarkStep(target, seconds, sample.MeanVideoMbps, sample.MeanFps, sample.ThrottleEvents, sample.ThrottleMs, sample.PcDroppedFrames, Late(), Reprojected(),
                _timed ? _encodeAverage.Average() : null, _timed ? _encodeMax : null, mean(_qp), sample.StreamResets, errors, result.Verdict, result.Reason, 1) { AskedMbps = mean(_asked), AskedMaxMbps = mean(_askedMax), ResetsPerSecond = perSecond };
            return new(result, step);
        }
    }
}

internal static class ReportFiles
{
    /// <summary>Deletes the json and csv saved for a run started at <paramref name="at"/>; files already gone are fine.</summary>
    public static void Delete(string directory, string prefix, DateTimeOffset at)
    {
        var stem = Path.Combine(directory, prefix + at.LocalDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        foreach (var extension in new[] { ".json", ".csv" })
            if (File.Exists(stem + extension)) File.Delete(stem + extension);
    }
}

/// <summary>Saves, loads and compares finished runs.</summary>
public static class BenchmarkReport
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Save(string directory, BenchmarkRun run)
    {
        Directory.CreateDirectory(directory);
        var stem = Path.Combine(directory, "benchmark-" + run.At.LocalDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(run, Json), new UTF8Encoding(false));
        File.WriteAllText(stem + ".csv", ToCsv(run), new UTF8Encoding(false));
        return stem + ".json";
    }

    /// <summary>Removes a saved run (its json and csv).</summary>
    public static void Delete(string directory, BenchmarkRun run) => ReportFiles.Delete(directory, "benchmark-", run.At);

    /// <summary>Earlier runs, newest first. Unreadable files are skipped.</summary>
    public static List<BenchmarkRun> LoadAll(string directory)
    {
        var runs = new List<BenchmarkRun>();
        if (!Directory.Exists(directory)) return runs;
        foreach (var file in Directory.EnumerateFiles(directory, "benchmark-*.json").OrderByDescending(f => f))
        {
            try { if (JsonSerializer.Deserialize<BenchmarkRun>(File.ReadAllText(file)) is { } run) runs.Add(run); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return runs;
    }

    public static string ToCsv(BenchmarkRun run)
    {
        var text = new StringBuilder();
        foreach (var line in new[] { "SteamFrame Codec Swapper benchmark", "started " + run.At.ToString("O", CultureInfo.InvariantCulture), "codec " + run.Codec }.Concat(run.Setup))
            text.Append("# ").AppendLine(line.Replace('\r', ' ').Replace('\n', ' '));
        text.Append("# ").AppendLine(run.Conclusion);
        text.AppendLine("target_mbps,seconds,mean_mbps,mean_fps,held_frames,held_per_minute,held_ms_per_minute,pc_dropped,pc_late,pc_reprojected,encode_avg_ms,encode_max_ms,avg_qp,stream_resets,encode_errors,verdict,passes,reason,asked_avg_mbps,asked_max_mbps");
        string N(double? v, string f = "0.0") => v is double d && double.IsFinite(d) ? d.ToString(f, CultureInfo.InvariantCulture) : "";
        foreach (var s in run.Steps)
            text.AppendLine(string.Join(',', new[]
            {
                s.TargetMbps.ToString(CultureInfo.InvariantCulture), N(s.Seconds), N(s.MeanMbps), N(s.MeanFps, "0.00"), s.HeldFrames.ToString(CultureInfo.InvariantCulture), N(s.HeldPerMinute), N(s.HeldMsPerMinute, "0"),
                s.PcDropped.ToString(CultureInfo.InvariantCulture), s.PcLate.ToString(CultureInfo.InvariantCulture), s.PcReprojected.ToString(CultureInfo.InvariantCulture), N(s.EncodeAverageMs, "0.00"), N(s.EncodeMaxMs, "0.00"), N(s.AverageQp, "0.00"),
                s.StreamResets.ToString(CultureInfo.InvariantCulture), s.EncodeErrors.ToString(CultureInfo.InvariantCulture), s.Verdict.ToString(), s.Passes.ToString(CultureInfo.InvariantCulture), "\"" + s.Reason.Replace("\"", "\"\"") + "\"", N(s.AskedMbps, "0"), N(s.AskedMaxMbps, "0")
            }));
        return text.ToString();
    }

    /// <summary>How this run compares with an earlier one, target by target, and what differed in the setup.</summary>
    public static string Compare(BenchmarkRun current, BenchmarkRun earlier)
    {
        var lines = new List<string> { $"Compared with {earlier.Title}:" };
        var differences = current.Setup.Except(earlier.Setup).Concat(earlier.Setup.Except(current.Setup).Select(s => "(earlier) " + s)).ToList();
        if (current.Codec != earlier.Codec) lines.Add($"Codec: {current.Codec} now, {earlier.Codec} then.");
        if (differences.Count > 0) lines.Add("Setup differs: " + string.Join("; ", differences) + ".");
        else if (current.Codec == earlier.Codec) lines.Add("Same codec and settings.");
        lines.Add(current.BestCleanMbps == earlier.BestCleanMbps
            ? (current.BestCleanMbps is int same ? $"Highest clean target is the same: {same} Mbps." : "Neither run had a clean step.")
            : $"Highest clean target: {Describe(current.BestCleanMbps)} now, {Describe(earlier.BestCleanMbps)} then.");
        foreach (var step in current.Steps)
        {
            if (earlier.Steps.FirstOrDefault(s => s.TargetMbps == step.TargetMbps) is not { } other) continue;
            lines.Add($"{step.TargetMbps} Mbps: held {step.HeldPerMinute:0}/min vs {other.HeldPerMinute:0}/min, PC drops {step.PcDropped} vs {other.PcDropped}, " +
                $"encode {Ms(step.EncodeAverageMs)} vs {Ms(other.EncodeAverageMs)}, {step.Verdict} vs {other.Verdict}.");
        }
        if (lines.Count <= 3) lines.Add("No target was measured in both runs.");
        return string.Join(Environment.NewLine, lines);

        static string Describe(int? mbps) => mbps is int m ? m + " Mbps" : "none";
        static string Ms(double? ms) => ms is double v ? v.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : "—";
    }
}
