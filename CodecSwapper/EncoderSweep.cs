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

    /// <summary>Every value of one option, lightest first, built on <paramref name="best"/>: the value already in use is one of them and is measured like the others.</summary>
    public static IEnumerable<EncoderChoice> Values(EncoderChoice best, EncoderDimension dimension) => dimension switch
    {
        EncoderDimension.Preset => Presets.Select(v => best with { Preset = v }),
        EncoderDimension.RateControl => RateControls.Select(v => best with { RateControl = v }),
        EncoderDimension.Vbv => VbvFrames.Select(v => best with { VbvFrames = v }),
        _ => MultiPasses.Select(v => best with { MultiPass = v }),
    };

    /// <summary>The value that makes a candidate heavier or riskier within its option (a bigger buffer, a higher preset or multipass); the rate control has no order.</summary>
    public static int Weight(EncoderChoice choice, EncoderDimension dimension) => dimension switch
    {
        EncoderDimension.Preset => choice.Preset,
        EncoderDimension.Vbv => choice.VbvFrames,
        EncoderDimension.MultiPass => choice.MultiPass,
        _ => 0,
    };

    public int CandidateCount => Enum.GetValues<EncoderDimension>().Sum(d => Values(new EncoderChoice("vbr", 2, 2, 0), d).Count());

    /// <summary>Every value is settled and measured, a better one is measured again before it is kept, plus a few confirmations and the final check.</summary>
    public TimeSpan Estimate => (Settle + Measure + TimeSpan.FromSeconds(5)) * (CandidateCount + 3 + 1) + TimeSpan.FromSeconds(20);
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
        var total = settings.CandidateCount + 1;
        var done = 0;
        EncoderChoice? bestSoFar = null;
        try
        {
            run.Codec = await _measure.PrepareAsync([settings.ProbeMbps], new Progress<BenchmarkProgress>(p => { }), token);

            async Task<BenchmarkStep> Window(string label)
            {
                var step = await _measure.MeasureStepAsync(settings.ProbeMbps, done, total, settings.Settle, settings.Measure, recheck: false,
                    new Progress<BenchmarkProgress>(p => progress?.Report(new(p.Phase, label, done, total, p.PhaseSecondsLeft, null))), token, waitQuiet: true);
                done++;
                return step;
            }

            void Record(EncoderTrial trial)
            {
                run.Trials.Add(trial);
                progress?.Report(new("done", trial.Label, done, total, 0, trial));
            }

            // The options are tested in order, one after the other, every value of each (the one in use included), lightest first. The first option is
            // tried on the user's other options as they are; each later one on top of the best found so far.
            var best = baseline;
            foreach (var dimension in Enum.GetValues<EncoderDimension>())
            {
                EncoderChoice? leader = null;
                BenchmarkStep? leaderStep = null;
                var failedAt = int.MaxValue;
                foreach (var candidate in EncoderSweepSettings.Values(best, dimension).OrderBy(c => EncoderSweepSettings.Weight(c, dimension)).ToList())
                {
                    token.ThrowIfCancellationRequested();
                    var label = Name(dimension, candidate) + (candidate == baseline || IsCurrentValue(dimension, candidate, baseline) ? " (current)" : "");
                    // Heavier (a bigger buffer, a higher preset) than one that failed after a lighter one worked is only going to be worse, and would hammer the stream again.
                    if (dimension != EncoderDimension.RateControl && EncoderSweepSettings.Weight(candidate, dimension) > failedAt)
                    {
                        Record(new EncoderTrial { Label = label, Choice = candidate, Step = (leaderStep ?? new BenchmarkStep(settings.ProbeMbps, 0, null, null, 0, 0, 0, 0, 0, null, null, null, 0, 0, SweepVerdict.Failed, "not tried", 1)) with { Verdict = SweepVerdict.Failed, Reason = "not tried" }, Decision = "not tried: a lighter setting already failed" });
                        continue;
                    }
                    await _apply(candidate, token);
                    var step = await Window(label);
                    var entry = new EncoderTrial { Label = label, Choice = candidate, Step = step };
                    if (step.MeanMbps is null)
                    {
                        // No video at all: the stream is gone (the headset disconnected or the encoder stalled). Nothing more can be measured, and the
                        // best settings found before this candidate are still worth keeping.
                        entry.Decision = "the stream was lost";
                        Record(entry);
                        throw new InvalidOperationException($"The stream was lost while trying {candidate.Describe()} (no video for the whole window), so the sweep stopped. The best settings found before that are kept as the recommendation; check the headset and SteamVR, and be careful with that option.");
                    }
                    var rough = step.Verdict == SweepVerdict.Failed || step.StreamResets > 0;
                    if (leader is null) { leader = candidate; leaderStep = step; entry.Decision = rough ? "first value tried (rough)" : "first value tried"; entry.Adopted = true; }
                    else
                    {
                        var order = EncoderRanking.Compare(step, leaderStep!);
                        var better = order < 0 || order == 0 && EncoderRanking.Prefers(dimension, candidate, leader) && !EncoderRanking.CostsMuchMore(step, leaderStep!);
                        if (!better) entry.Decision = order > 0 ? "less smooth" : "no better";
                        else
                        {
                            // Looking better once may be noise on a link like Wi-Fi: it has to hold up in a second window to take the lead.
                            var again = await Window(label + " (confirming)");
                            var second = EncoderRanking.Compare(again, leaderStep!);
                            if (second < 0 || second == 0 && EncoderRanking.Prefers(dimension, candidate, leader) && !EncoderRanking.CostsMuchMore(again, leaderStep!))
                            {
                                entry.Decision = "takes the lead"; entry.Adopted = true;
                                leader = candidate; leaderStep = Worse(step, again);
                            }
                            else entry.Decision = "did not hold up when measured again";
                        }
                    }
                    Record(entry);
                    if (rough)
                    {
                        // A heavier value than a rough one is skipped only once a lighter value has worked.
                        if (leader != candidate) failedAt = Math.Min(failedAt, EncoderSweepSettings.Weight(candidate, dimension));
                        // Back to the best settings, and wait for the stream to calm down: measuring the next value in the middle of a recovery storm measures the storm.
                        await _apply(leader ?? best, token);
                        if (!await _measure.WaitQuietAsync(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(60), null, token))
                            throw new InvalidOperationException($"The stream did not settle after {candidate.Describe()} (it kept asking for recovery), so the sweep stopped to protect it. The best settings found so far are kept as the recommendation.");
                    }
                    bestSoFar = leader;
                }
                best = leader ?? best;
                bestSoFar = best;
                // The next option is tried on top of the best so far, so the best has to be what is running.
                await _apply(best, token);
            }

            // One last window on the combination chosen, so the recommendation has been measured as a whole.
            await _apply(best, token);
            var final = await Window("final choice");
            Record(new EncoderTrial { Label = "Final choice", Choice = best, Step = final, Decision = final.Verdict == SweepVerdict.Clean ? "the chosen combination, measured as a whole" : "the chosen combination was not clean as a whole: run again, or at a lower bitrate", Adopted = true });
            run.Best = best;
        }
        catch (OperationCanceledException) { run.Stopped = true; }
        catch (InvalidOperationException error)
        {
            run.Error = error.Message;
            if (run.Error.StartsWith("The stream did not settle", StringComparison.Ordinal) || run.Error.StartsWith("The stream was lost", StringComparison.Ordinal)) run.Best = bestSoFar;
        }
        run.Conclusion = Conclude(run);
        return run;
    }

    private static bool IsCurrentValue(EncoderDimension dimension, EncoderChoice candidate, EncoderChoice baseline) => dimension switch
    {
        EncoderDimension.Preset => candidate.Preset == baseline.Preset,
        EncoderDimension.RateControl => candidate.RateControl == baseline.RateControl,
        EncoderDimension.Vbv => candidate.VbvFrames == baseline.VbvFrames,
        _ => candidate.MultiPass == baseline.MultiPass,
    };

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
        var measured = run.Trials.Where(t => !t.Decision.StartsWith("not tried", StringComparison.Ordinal) && t.Label != "Final choice").ToList();
        if (run.Best is null) text.Append("The sweep ended before a recommendation could be made. ");
        else if (run.Best == run.Baseline) text.Append($"Your current settings ({run.Baseline.Describe()}) came out as the smoothest of those tried at {run.ProbeMbps} Mbps. ");
        else
        {
            text.Append($"Smoothest at {run.ProbeMbps} Mbps: {run.Best.Describe()} (now {run.Baseline.Describe()}). ");
            var final = run.Trials.LastOrDefault(t => t.Label == "Final choice")?.Step;
            // The step measured with the user's own value of the first option, on their other options as they were, is the nearest thing to "before".
            var before = measured.FirstOrDefault(t => t.Choice == run.Baseline)?.Step;
            if (before is not null && final is not null)
            {
                text.Append($"Held-back time {before.HeldMsPerMinute:0} → {final.HeldMsPerMinute:0} ms/min, encode time {before.EncodeAverageMs?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—"} → {final.EncodeAverageMs?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—"} ms. ");
                // Smoother can simply mean sending less (a one-frame buffer caps the bursts); say so, so the trade is a choice.
                if (before.MeanMbps is double was && final.MeanMbps is double now && was > 0 && now < 0.9 * was)
                    text.Append($"Note: it delivers {now:0} Mbps instead of {was:0} Mbps ({(1 - now / was) * 100:0}% less), so part of the smoothness comes from sending less. ");
            }
        }
        if (measured.Count > 0 && measured.All(t => t.Step.Verdict == SweepVerdict.Clean && t.Step.HeldFrames == 0))
            text.Append($"Every setting streamed cleanly at {run.ProbeMbps} Mbps, so the differences are in picture quality and encode time, not smoothness. Run it at a higher bitrate (a little below where your link starts to fail) to tell them apart. ");
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
