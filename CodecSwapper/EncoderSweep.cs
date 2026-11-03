using System.Globalization;
using System.Text;
using System.Text.Json;
using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>The encoder options a sweep tries: the four the tray can change live.</summary>
public sealed record EncoderChoice(string RateControl, int Preset, int VbvFrames, int MultiPass)
{
    public string Describe() => $"{RateControl.ToUpperInvariant()}, P{Preset}, VBV {VbvFrames}, multipass {MultiPass}";

    public static EncoderChoice From(FramePreferences preferences) => new(preferences.HookRateControl == "cbr" ? "cbr" : "vbr", preferences.HookPreset, preferences.HookVbvFrames, preferences.HookMultiPass);
}

public enum EncoderDimension { Preset, RateControl, Vbv, MultiPass }

/// <param name="ProbeMbps">The one bitrate every candidate is measured at, so settings are compared with each other and not with the link.</param>
public sealed record EncoderSweepSettings(int ProbeMbps, TimeSpan Settle, TimeSpan Measure)
{
    public static readonly int[] Presets = [1, 2, 4, 6, 7], VbvFrames = [1, 2, 4, 8], MultiPasses = [0, 1, 2];
    public static readonly string[] RateControls = ["vbr", "cbr"];

    /// <summary>Every value of one option, in the order they are tried.</summary>
    public static IEnumerable<EncoderChoice> Alternatives(EncoderChoice best, EncoderDimension dimension) => dimension switch
    {
        EncoderDimension.Preset => Presets.Where(v => v != best.Preset).Select(v => best with { Preset = v }),
        EncoderDimension.RateControl => RateControls.Where(v => v != best.RateControl).Select(v => best with { RateControl = v }),
        EncoderDimension.Vbv => VbvFrames.Where(v => v != best.VbvFrames).Select(v => best with { VbvFrames = v }),
        _ => MultiPasses.Where(v => v != best.MultiPass).Select(v => best with { MultiPass = v }),
    };

    /// <summary>The value that makes a candidate heavier or riskier within its option (a bigger buffer, a higher preset or multipass); the rate control has no order.</summary>
    public static int Weight(EncoderChoice choice, EncoderDimension dimension) => dimension switch
    {
        EncoderDimension.Preset => choice.Preset,
        EncoderDimension.Vbv => choice.VbvFrames,
        EncoderDimension.MultiPass => choice.MultiPass,
        _ => 0,
    };

    public int CandidateCount => Enum.GetValues<EncoderDimension>().Sum(d => Alternatives(new EncoderChoice("vbr", 2, 2, 0), d).Count());

    /// <summary>Each candidate is settled and measured, a better one is measured again before it is adopted, plus the baseline and the final check.</summary>
    public TimeSpan Estimate => (Settle + Measure) * (CandidateCount + 1 + 3) + TimeSpan.FromSeconds(20);
}

public sealed class EncoderTrial
{
    public string Label { get; set; } = "";
    public EncoderChoice Choice { get; set; } = new("vbr", 2, 2, 0);
    public BenchmarkStep Step { get; set; } = null!;
    /// <summary>What the sweep did with it: kept as the new best, or why not.</summary>
    public string Decision { get; set; } = "";
    public bool Adopted { get; set; }
}

public sealed class EncoderSweepRun
{
    public DateTimeOffset At { get; set; }
    public string Codec { get; set; } = "";
    public List<string> Setup { get; set; } = [];
    public int ProbeMbps { get; set; }
    public int MeasureSeconds { get; set; }
    public EncoderChoice Baseline { get; set; } = new("vbr", 2, 2, 0);
    public EncoderChoice? Best { get; set; }
    public List<EncoderTrial> Trials { get; set; } = [];
    public string Conclusion { get; set; } = "";
    public bool Stopped { get; set; }
    public string? Error { get; set; }

    public string Title => $"{At.LocalDateTime:MMM d HH:mm} · {Codec} · encoder sweep at {ProbeMbps} Mbps" + (Best is { } best ? " · " + best.Describe() : "") + (Stopped ? " (stopped)" : "");
}

/// <summary>How two measurements of the same bitrate compare for the "smoothest" goal. Differences inside the noise count as equal.</summary>
public static class EncoderRanking
{
    private const double HeldMsMargin = 25, DropsMargin = 2, EncodeMargin = 0.5, HeadroomShare = 0.6;

    private static int Class(SweepVerdict verdict) => verdict switch { SweepVerdict.Clean => 0, SweepVerdict.Marginal => 1, _ => 2 };

    /// <summary>Whether the encoder's slowest frame fits well inside one frame interval, so a busy game has room left.</summary>
    public static bool HasHeadroom(BenchmarkStep step) => step.EncodeMaxMs is not double max || step.MeanFps is not double fps || fps <= 0 || max <= HeadroomShare * 1000.0 / fps;

    /// <summary>Negative when <paramref name="a"/> is smoother than <paramref name="b"/>, positive when it is worse, zero when they are the same within the noise.</summary>
    public static int Compare(BenchmarkStep a, BenchmarkStep b)
    {
        if (Class(a.Verdict) != Class(b.Verdict)) return Class(a.Verdict).CompareTo(Class(b.Verdict));
        var resets = (a.StreamResets * 60.0 / Math.Max(1, a.Seconds)) - (b.StreamResets * 60.0 / Math.Max(1, b.Seconds));
        if (Math.Abs(resets) >= 2) return resets < 0 ? -1 : 1;
        var held = a.HeldMsPerMinute - b.HeldMsPerMinute;
        if (Math.Abs(held) > Math.Max(HeldMsMargin, 0.25 * Math.Max(a.HeldMsPerMinute, b.HeldMsPerMinute))) return held < 0 ? -1 : 1;
        var drops = a.PcDropped * 60.0 / Math.Max(1, a.Seconds) - b.PcDropped * 60.0 / Math.Max(1, b.Seconds);
        if (Math.Abs(drops) > DropsMargin) return drops < 0 ? -1 : 1;
        if (HasHeadroom(a) != HasHeadroom(b)) return HasHeadroom(a) ? -1 : 1;
        return 0;
    }

    /// <summary>Among equally smooth settings: a heavier preset or multipass is the better picture and a smaller buffer the lower latency; the rate control stays as it is.</summary>
    public static bool Prefers(EncoderDimension dimension, EncoderChoice candidate, EncoderChoice best) => dimension switch
    {
        EncoderDimension.Preset => candidate.Preset > best.Preset,
        EncoderDimension.Vbv => candidate.VbvFrames < best.VbvFrames,
        EncoderDimension.MultiPass => candidate.MultiPass > best.MultiPass,
        _ => false,
    };

    /// <summary>The encode time is part of a tie: a heavier choice must not cost much more than the one it replaces.</summary>
    public static bool CostsMuchMore(BenchmarkStep candidate, BenchmarkStep best) => candidate.EncodeAverageMs is double c && best.EncodeAverageMs is double b && c > b + EncodeMargin * Math.Max(1, b) * 4;
}

public sealed record EncoderSweepProgress(string Phase, string Candidate, int Done, int Total, double SecondsLeft, EncoderTrial? Finished);

/// <summary>
/// Finds the smoothest encoder settings for the stream that is running. It measures the current settings, then tries the options one at a time
/// (preset, rate control, VBV, multipass), always starting from the best found so far, all at the same bitrate. A candidate that looks better
/// is measured a second time before it is kept, and the result is checked against the starting settings at the end.
/// </summary>
public sealed class EncoderSweepRunner
{
    private readonly BenchmarkRunner _measure;
    private readonly Func<EncoderChoice, CancellationToken, Task> _apply;

    /// <param name="measure">Measures one window at the current settings.</param>
    /// <param name="apply">Puts the encoder options into force on the running stream.</param>
    public EncoderSweepRunner(BenchmarkRunner measure, Func<EncoderChoice, CancellationToken, Task> apply)
    {
        _measure = measure; _apply = apply;
    }

    public async Task<EncoderSweepRun> RunAsync(EncoderSweepSettings settings, EncoderSweepRun run, EncoderChoice baseline, IProgress<EncoderSweepProgress>? progress, CancellationToken token)
    {
        run.ProbeMbps = settings.ProbeMbps; run.MeasureSeconds = (int)settings.Measure.TotalSeconds; run.Baseline = baseline;
        var total = settings.CandidateCount + 4;
        var done = 0;
        var inner = new Progress<BenchmarkProgress>(p => { });
        EncoderChoice? bestSoFar = null;
        try
        {
            run.Codec = await _measure.PrepareAsync([settings.ProbeMbps], inner, token);

            async Task<BenchmarkStep> Window(string label)
            {
                var step = await _measure.MeasureStepAsync(settings.ProbeMbps, done, total, settings.Settle, settings.Measure, recheck: false,
                    new Progress<BenchmarkProgress>(p => progress?.Report(new(p.Phase, label, done, total, p.PhaseSecondsLeft, null))), token);
                done++;
                return step;
            }

            await _apply(baseline, token);
            var best = baseline;
            bestSoFar = best;
            var bestStep = await Window("current settings");
            var trial = new EncoderTrial { Label = "Current settings", Choice = baseline, Step = bestStep, Decision = "starting point", Adopted = true };
            run.Trials.Add(trial);
            progress?.Report(new("done", trial.Label, done, total, 0, trial));

            foreach (var dimension in Enum.GetValues<EncoderDimension>())
            {
                var failedAt = int.MaxValue;
                foreach (var candidate in EncoderSweepSettings.Alternatives(best, dimension).OrderBy(c => EncoderSweepSettings.Weight(c, dimension)).ToList())
                {
                    token.ThrowIfCancellationRequested();
                    // Heavier (a bigger buffer, a higher preset) than one that already failed is only going to be worse, and it would hammer the stream again.
                    if (EncoderSweepSettings.Weight(candidate, dimension) > failedAt && dimension != EncoderDimension.RateControl)
                    {
                        run.Trials.Add(new EncoderTrial { Label = Name(dimension, candidate), Choice = candidate, Step = bestStep with { Verdict = SweepVerdict.Failed, Reason = "not tried" }, Decision = "not tried: a lighter setting already failed" });
                        continue;
                    }
                    await _apply(candidate, token);
                    var step = await Window(candidate.Describe());
                    var entry = new EncoderTrial { Label = Name(dimension, candidate), Choice = candidate, Step = step };
                    if (step.Verdict == SweepVerdict.Failed || step.StreamResets > 0)
                    {
                        if (EncoderSweepSettings.Weight(candidate, dimension) > EncoderSweepSettings.Weight(best, dimension)) failedAt = Math.Min(failedAt, EncoderSweepSettings.Weight(candidate, dimension));
                        // Back to the best settings, and wait for the stream to calm down: measuring the next candidate in the middle of a recovery storm measures the storm.
                        await _apply(best, token);
                        if (!await _measure.WaitQuietAsync(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(60), null, token))
                        {
                            entry.Decision = "stream did not recover";
                            run.Trials.Add(entry);
                            progress?.Report(new("done", entry.Label, done, total, 0, entry));
                            throw new InvalidOperationException($"The stream did not settle after {candidate.Describe()} (it kept asking for recovery), so the sweep stopped to protect it. The best settings found so far are kept as the recommendation.");
                        }
                    }
                    var order = EncoderRanking.Compare(step, bestStep);
                    var better = order < 0 || order == 0 && EncoderRanking.Prefers(dimension, candidate, best) && !EncoderRanking.CostsMuchMore(step, bestStep);
                    if (!better) entry.Decision = order > 0 ? "less smooth" : "no better";
                    else
                    {
                        // Looking better once may be noise on a link like Wi-Fi: it has to hold up in a second window to be kept.
                        var again = await Window(candidate.Describe() + " (confirming)");
                        var second = EncoderRanking.Compare(again, bestStep);
                        var stays = second < 0 || second == 0 && EncoderRanking.Prefers(dimension, candidate, best) && !EncoderRanking.CostsMuchMore(again, bestStep);
                        if (stays)
                        {
                            entry.Decision = "kept as the new best"; entry.Adopted = true;
                            best = candidate; bestStep = Worse(step, again); bestSoFar = best;
                        }
                        else entry.Decision = "did not hold up when measured again";
                    }
                    run.Trials.Add(entry);
                    progress?.Report(new("done", entry.Label, done, total, 0, entry));
                }
                // The next option is tried on top of the best so far, so the best has to be what is running.
                await _apply(best, token);
            }

            if (best != baseline)
            {
                await _apply(baseline, token);
                var starting = await Window("current settings again");
                await _apply(best, token);
                var final = await Window("final choice");
                var verdict = EncoderRanking.Compare(final, starting);
                run.Trials.Add(new EncoderTrial { Label = "Current settings again", Choice = baseline, Step = starting, Decision = "final check" });
                run.Trials.Add(new EncoderTrial { Label = "Final choice", Choice = best, Step = final, Decision = verdict <= 0 ? "at least as smooth as the current settings" : "worse than the current settings in the final check", Adopted = verdict <= 0 });
                if (verdict > 0) best = baseline;
            }
            run.Best = best;
        }
        catch (OperationCanceledException) { run.Stopped = true; }
        catch (InvalidOperationException error) { run.Error = error.Message; if (run.Error.StartsWith("The stream did not settle", StringComparison.Ordinal)) run.Best = bestSoFar; }
        run.Conclusion = Conclude(run);
        return run;
    }

    private static string Name(EncoderDimension dimension, EncoderChoice choice) => dimension switch
    {
        EncoderDimension.Preset => $"Preset P{choice.Preset}",
        EncoderDimension.RateControl => "Rate control " + choice.RateControl.ToUpperInvariant(),
        EncoderDimension.Vbv => $"VBV {choice.VbvFrames} frame(s)",
        _ => $"Multipass {choice.MultiPass}",
    };

    private static BenchmarkStep Worse(BenchmarkStep a, BenchmarkStep b) => EncoderRanking.Compare(a, b) >= 0 ? a : b;

    public static string Conclude(EncoderSweepRun run)
    {
        if (run.Trials.Count == 0) return run.Error ?? "Nothing was measured.";
        var text = new StringBuilder();
        var baseline = run.Trials[0].Step;
        if (run.Best is null) text.Append("The sweep ended before a recommendation could be made. ");
        else if (run.Best == run.Baseline) text.Append($"Your current settings ({run.Baseline.Describe()}) were already the smoothest of those tried at {run.ProbeMbps} Mbps. ");
        else
        {
            var chosen = run.Trials.LastOrDefault(t => t.Adopted && t.Choice == run.Best)?.Step ?? baseline;
            text.Append($"Smoothest at {run.ProbeMbps} Mbps: {run.Best.Describe()} (now {run.Baseline.Describe()}). ");
            text.Append($"Held-back time {baseline.HeldMsPerMinute:0} → {chosen.HeldMsPerMinute:0} ms/min, encode time {baseline.EncodeAverageMs?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—"} → {chosen.EncodeAverageMs?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—"} ms. ");
        }
        if (run.Trials.Where(t => !t.Decision.StartsWith("not tried", StringComparison.Ordinal)).All(t => t.Step.Verdict == SweepVerdict.Clean && t.Step.HeldFrames == 0))
            text.Append($"Every setting streamed cleanly at {run.ProbeMbps} Mbps, so the differences are in picture quality and encode time, not smoothness. Run it at a higher bitrate (near where your link starts to fail) to tell them apart. ");
        if (run.Stopped) text.Append("The run was stopped early. ");
        if (run.Error is not null) text.Append(run.Error);
        return text.ToString().Trim();
    }
}

public static class EncoderSweepReport
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Save(string directory, EncoderSweepRun run)
    {
        Directory.CreateDirectory(directory);
        var stem = Path.Combine(directory, "encoder-sweep-" + run.At.LocalDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(run, Json), new UTF8Encoding(false));
        File.WriteAllText(stem + ".csv", ToCsv(run), new UTF8Encoding(false));
        return stem + ".json";
    }

    /// <summary>Earlier sweeps, newest first. Unreadable files are skipped.</summary>
    public static List<EncoderSweepRun> LoadAll(string directory)
    {
        var runs = new List<EncoderSweepRun>();
        if (!Directory.Exists(directory)) return runs;
        foreach (var file in Directory.EnumerateFiles(directory, "encoder-sweep-*.json").OrderByDescending(f => f))
        {
            try { if (JsonSerializer.Deserialize<EncoderSweepRun>(File.ReadAllText(file)) is { } run) runs.Add(run); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return runs;
    }

    public static string ToCsv(EncoderSweepRun run)
    {
        var text = new StringBuilder();
        foreach (var line in new[] { "SteamFrame Codec Swapper encoder sweep", "started " + run.At.ToString("O", CultureInfo.InvariantCulture), "codec " + run.Codec, $"probe {run.ProbeMbps} Mbps" }.Concat(run.Setup))
            text.Append("# ").AppendLine(line.Replace('\r', ' ').Replace('\n', ' '));
        text.Append("# ").AppendLine(run.Conclusion);
        text.AppendLine("candidate,rate_control,preset,vbv_frames,multipass,seconds,mean_mbps,held_frames,held_ms_per_minute,pc_dropped,stream_resets,encode_avg_ms,encode_max_ms,verdict,decision");
        string N(double? v, string f = "0.0") => v is double d && double.IsFinite(d) ? d.ToString(f, CultureInfo.InvariantCulture) : "";
        foreach (var t in run.Trials)
            text.AppendLine(string.Join(',', new[]
            {
                "\"" + t.Label.Replace("\"", "\"\"") + "\"", t.Choice.RateControl, t.Choice.Preset.ToString(CultureInfo.InvariantCulture), t.Choice.VbvFrames.ToString(CultureInfo.InvariantCulture), t.Choice.MultiPass.ToString(CultureInfo.InvariantCulture),
                N(t.Step.Seconds), N(t.Step.MeanMbps), t.Step.HeldFrames.ToString(CultureInfo.InvariantCulture), N(t.Step.HeldMsPerMinute, "0"), t.Step.PcDropped.ToString(CultureInfo.InvariantCulture),
                t.Step.StreamResets.ToString(CultureInfo.InvariantCulture), N(t.Step.EncodeAverageMs, "0.00"), N(t.Step.EncodeMaxMs, "0.00"), t.Step.Verdict.ToString(), "\"" + t.Decision + "\""
            }));
        return text.ToString();
    }
}
