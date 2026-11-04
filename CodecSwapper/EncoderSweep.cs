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

/// <summary>What a run is after: the smoothest stream, the best picture that still streams acceptably, or just a look at the settings in use (nothing is changed).</summary>
public enum EncoderGoal { Smoothest, Picture, Current }

/// <param name="ProbeMbps">The one bitrate every candidate is measured at, so settings are compared with each other and not with the link.</param>
public sealed record EncoderSweepSettings(int ProbeMbps, TimeSpan Settle, TimeSpan Measure, EncoderGoal Goal = EncoderGoal.Smoothest)
{
    public static readonly int[] Presets = [1, 2, 4, 6, 7], VbvFrames = [1, 2, 4, 8], MultiPasses = [0, 1, 2];
    public static readonly string[] RateControls = ["vbr", "cbr"];

    /// <summary>Where every sweep starts: the lightest value of each option, so nothing the user happens to have set (a multipass, a big buffer) colours the first measurements.</summary>
    public static readonly EncoderChoice Lowest = new("vbr", Presets[0], VbvFrames[0], MultiPasses[0]);

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

    /// <summary>The order the options are tried in: the multipass before the buffer, because a multipass lets a small buffer reach a bitrate it falls short of without one.</summary>
    public static readonly EncoderDimension[] Order = [EncoderDimension.Preset, EncoderDimension.RateControl, EncoderDimension.MultiPass, EncoderDimension.Vbv];

    public int CandidateCount => Enum.GetValues<EncoderDimension>().Sum(d => Values(new EncoderChoice("vbr", 2, 2, 0), d).Count());

    /// <summary>Every value is settled and measured, a better one is measured again before it is kept, plus a few confirmations and the final check.</summary>
    public TimeSpan Estimate => Goal == EncoderGoal.Current ? Settle + Measure + TimeSpan.FromSeconds(20) : (Settle + Measure + TimeSpan.FromSeconds(5)) * (CandidateCount + 6 + 2) + TimeSpan.FromSeconds(20);
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
    public EncoderGoal Goal { get; set; }
    public int MeasureSeconds { get; set; }
    public EncoderChoice Baseline { get; set; } = new("vbr", 2, 2, 0);
    public EncoderChoice? Best { get; set; }
    public List<EncoderTrial> Trials { get; set; } = [];
    public string Conclusion { get; set; } = "";
    public bool Stopped { get; set; }
    public string? Error { get; set; }

    public string Title => $"{At.LocalDateTime:MMM d HH:mm} · {Codec} · " + Goal switch { EncoderGoal.Picture => "best-picture sweep", EncoderGoal.Current => "current settings test", _ => "encoder sweep" } + $" at {ProbeMbps} Mbps" + (Best is { } best ? " · " + best.Describe() : "") + (Stopped ? " (stopped)" : "");
}

/// <summary>How two measurements of the same bitrate compare for the "smoothest" goal. Differences inside the noise count as equal.</summary>
public static class EncoderRanking
{
    private const double HeldMsMargin = 25, DropsMargin = 2, EncodeMargin = 0.5, HeadroomShare = 0.6, ReachMargin = 0.05;

    /// <summary>One or two recovery requests in a window are what a change of options causes; a difference of two or more in a 15 s window (8 per minute) is real.</summary>
    private const double ResetsPerMinuteMargin = 8;

    private static int Class(SweepVerdict verdict) => verdict switch { SweepVerdict.Clean => 0, SweepVerdict.Marginal => 1, _ => 2 };

    /// <summary>Whether the encoder's slowest frame fits well inside one frame interval, so a busy game has room left.</summary>
    public static bool HasHeadroom(BenchmarkStep step) => step.EncodeMaxMs is not double max || step.MeanFps is not double fps || fps <= 0 || max <= HeadroomShare * 1000.0 / fps;

    /// <summary>Negative when <paramref name="a"/> is smoother than <paramref name="b"/>, positive when it is worse, zero when they are the same within the noise.</summary>
    public static int Compare(BenchmarkStep a, BenchmarkStep b)
    {
        if (Class(a.Verdict) != Class(b.Verdict)) return Class(a.Verdict).CompareTo(Class(b.Verdict));
        var resets = (a.StreamResets * 60.0 / Math.Max(1, a.Seconds)) - (b.StreamResets * 60.0 / Math.Max(1, b.Seconds));
        if (Math.Abs(resets) >= ResetsPerMinuteMargin) return resets < 0 ? -1 : 1;
        var held = a.HeldMsPerMinute - b.HeldMsPerMinute;
        if (Math.Abs(held) > Math.Max(HeldMsMargin, 0.25 * Math.Max(a.HeldMsPerMinute, b.HeldMsPerMinute))) return held < 0 ? -1 : 1;
        var drops = a.PcDropped * 60.0 / Math.Max(1, a.Seconds) - b.PcDropped * 60.0 / Math.Max(1, b.Seconds);
        if (Math.Abs(drops) > DropsMargin) return drops < 0 ? -1 : 1;
        if (HasHeadroom(a) != HasHeadroom(b)) return HasHeadroom(a) ? -1 : 1;
        // Equally smooth: the one that gets closer to the bitrate asked for is the better picture (a small buffer can look smooth just by sending less).
        var reach = Reach(a) - Reach(b);
        if (Math.Abs(reach) > ReachMargin) return reach > 0 ? -1 : 1;
        return 0;
    }

    /// <summary>How much of the asked-for bitrate was delivered, at most all of it (more than asked is not better).</summary>
    public static double Reach(BenchmarkStep step) => step.MeanMbps is double mbps && step.TargetMbps > 0 ? Math.Min(1, mbps / step.TargetMbps) : 0;

    /// <summary>Among equally smooth settings: a heavier preset or multipass is the better picture and a smaller buffer the lower latency; the rate control stays as it is.</summary>
    public static bool Prefers(EncoderDimension dimension, EncoderChoice candidate, EncoderChoice best) => dimension switch
    {
        EncoderDimension.Preset => candidate.Preset > best.Preset,
        EncoderDimension.Vbv => candidate.VbvFrames < best.VbvFrames,
        EncoderDimension.MultiPass => candidate.MultiPass > best.MultiPass,
        _ => false,
    };

    /// <summary>
    /// The one choice among everything measured, whatever order it was measured in: the smoothest by <see cref="Compare"/>, and among those equally smooth the
    /// one that delivers the most of the bitrate, then the heavier preset, the heavier multipass and the smaller buffer.
    /// </summary>
    public static (EncoderChoice Choice, string Why)? Select(IReadOnlyDictionary<EncoderChoice, BenchmarkStep> measured, EncoderGoal goal = EncoderGoal.Smoothest)
    {
        if (measured.Count == 0) return null;
        var usable = measured.Where(p => p.Value.Verdict != SweepVerdict.Failed).ToList();
        if (usable.Count == 0) usable = measured.ToList();
        usable = usable.OrderBy(p => p.Key.RateControl == "vbr" ? 0 : 1).ThenBy(p => p.Key.Preset).ThenBy(p => p.Key.MultiPass).ThenBy(p => p.Key.VbvFrames).ToList();
        if (goal == EncoderGoal.Picture) return SelectPicture(usable);
        var smoothest = usable[0];
        foreach (var p in usable.Skip(1)) if (Compare(p.Value, smoothest.Value) < 0) smoothest = p;
        var tied = usable.Where(p => Compare(p.Value, smoothest.Value) == 0 && !CostsMuchMore(p.Value, smoothest.Value)).ToList();
        if (tied.Count == 0) tied.Add(smoothest);
        var pick = tied[0];
        foreach (var p in tied.Skip(1))
        {
            var reach = Reach(p.Value) - Reach(pick.Value);
            if (reach > ReachMargin || Math.Abs(reach) <= ReachMargin && Heavier(p.Key, pick.Key)) pick = p;
        }
        if (tied.Count == 1) return (pick.Key, "the smoothest of those measured");
        var higherReach = tied.Any(p => p.Key != pick.Key && Reach(pick.Value) - Reach(p.Value) > ReachMargin);
        return (pick.Key, $"{tied.Count} settings were equally smooth; picked for " + (higherReach ? "delivering more of the bitrate" : "the heavier settings (better picture)"));
    }

    /// <summary>
    /// Best picture: among the settings that did not fail and leave the encoder room, the one that delivers the most of the bitrate asked for; settings within 5% of
    /// that count as equal, and of those the smoothest wins, then the heavier preset, the heavier multipass and the smaller buffer.
    /// </summary>
    private static (EncoderChoice Choice, string Why) SelectPicture(List<KeyValuePair<EncoderChoice, BenchmarkStep>> usable)
    {
        var pool = usable.Where(p => HasHeadroom(p.Value)).ToList();
        if (pool.Count == 0) pool = usable;
        var top = pool.Max(p => Reach(p.Value));
        var near = pool.Where(p => top - Reach(p.Value) <= ReachMargin).ToList();
        var smooth = near[0];
        foreach (var p in near.Skip(1)) if (Compare(p.Value, smooth.Value) < 0) smooth = p;
        var tied = near.Where(p => Compare(p.Value, smooth.Value) == 0 && !CostsMuchMore(p.Value, smooth.Value)).ToList();
        if (tied.Count == 0) tied.Add(smooth);
        var pick = tied[0];
        foreach (var p in tied.Skip(1)) if (Heavier(p.Key, pick.Key)) pick = p;
        var why = $"best picture: delivered {Reach(pick.Value) * 100:0}% of the bitrate asked for" + (near.Count > 1 ? $"; {near.Count} settings were within 5% of that, picked the smoothest of them and then the heavier settings" : "");
        return (pick.Key, why);
    }

    private static bool Heavier(EncoderChoice a, EncoderChoice b) => a.Preset != b.Preset ? a.Preset > b.Preset : a.MultiPass != b.MultiPass ? a.MultiPass > b.MultiPass : a.VbvFrames < b.VbvFrames;

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
        if (settings.Goal == EncoderGoal.Current) return await RunCurrentAsync(settings, run, baseline, progress, token);
        run.Goal = settings.Goal;
        run.ProbeMbps = settings.ProbeMbps; run.MeasureSeconds = (int)settings.Measure.TotalSeconds; run.Baseline = baseline;
        var total = settings.CandidateCount + 2;   // grows by one for each repeat measurement, so the count never runs past the total
        var done = 0;
        EncoderChoice? bestSoFar = null;
        var measured = new Dictionary<EncoderChoice, BenchmarkStep>();
        var confirmed = new HashSet<EncoderChoice>();
        var skipped = new HashSet<EncoderChoice>();
        try
        {
            run.Codec = await _measure.PrepareAsync([settings.ProbeMbps], new Progress<BenchmarkProgress>(p => { }), token);
            // Everything starts at the lowest value and works its way up; the user's own combination is measured at the end for comparison.
            await _apply(EncoderSweepSettings.Lowest, token);
            // Right after the codec switch and the scene starting, the stream is still settling and asks for fresh pictures all the time; the first value would take the blame.
            progress?.Report(new("waiting for the stream to settle", "start", 0, total, 0, null));
            if (!await _measure.WaitQuietAsync(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(90), null, token))
                throw new InvalidOperationException("The stream did not settle before the first measurement (it kept asking for recovery for 90 seconds), so the sweep did not start. Check the link and that the headset is connected, then try again.");

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

            InvalidOperationException Lost(EncoderChoice candidate) => new($"The stream was lost while trying {candidate.Describe()} (no video for the whole window), so the sweep stopped. The best settings found before that are kept as the recommendation; check the headset and SteamVR, and be careful with that option.");

            // The options are tried in order, every value of each (lightest first) on top of the best found so far, and the best is chosen from everything
            // measured, by one fixed ranking. The passes repeat until the best stops changing, so the order the options are tried in does not decide the result.
            var best = EncoderSweepSettings.Lowest;
            for (var pass = 1; pass <= MaxPasses; pass++)
            {
                var passStart = best;
                foreach (var dimension in EncoderSweepSettings.Order)
                {
                    var failedAt = int.MaxValue;
                    var lighterWorked = false;
                    foreach (var candidate in EncoderSweepSettings.Values(best, dimension).OrderBy(c => EncoderSweepSettings.Weight(c, dimension)).ToList())
                    {
                        token.ThrowIfCancellationRequested();
                        var weight = EncoderSweepSettings.Weight(candidate, dimension);
                        if (measured.TryGetValue(candidate, out var known))
                        {
                            // Already measured on an earlier pass (or by another option's turn): not measured again.
                            if (known.Verdict == SweepVerdict.Failed) { if (lighterWorked && dimension != EncoderDimension.RateControl) failedAt = Math.Min(failedAt, weight); }
                            else lighterWorked = true;
                            continue;
                        }
                        var label = Name(dimension, candidate) + (IsCurrentValue(dimension, candidate, baseline) ? " (current)" : "");
                        // Heavier (a bigger buffer, a higher preset) than one that failed after a lighter one worked is only going to be worse, and would hammer the stream again.
                        if (dimension != EncoderDimension.RateControl && weight > failedAt)
                        {
                            if (skipped.Add(candidate))
                                Record(new EncoderTrial { Label = label, Choice = candidate, Step = NotTried(settings), Decision = "not tried: a lighter setting already failed" });
                            continue;
                        }
                        await _apply(candidate, token);
                        total = Math.Max(total, done + 3);
                        var step = await Window(label);
                        var entry = new EncoderTrial { Label = label, Choice = candidate, Step = step };
                        if (step.MeanMbps is null)
                        {
                            // No video at all: the stream is gone (the headset disconnected or the encoder stalled). Nothing more can be measured, and the
                            // best settings found before this candidate are still worth keeping.
                            entry.Decision = "the stream was lost";
                            Record(entry);
                            throw Lost(candidate);
                        }
                        measured[candidate] = step;
                        var rough = step.Verdict == SweepVerdict.Failed;
                        entry.Decision = rough ? "rough" : "measured";
                        Record(entry);
                        if (rough)
                        {
                            if (lighterWorked && dimension != EncoderDimension.RateControl) failedAt = Math.Min(failedAt, weight);
                            // Back to the best settings, and wait for the stream to calm down: measuring the next value in the middle of a recovery storm measures the storm.
                            await _apply(best, token);
                            if (!await _measure.WaitQuietAsync(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(60), null, token))
                                throw new InvalidOperationException($"The stream did not settle after {candidate.Describe()} (it kept asking for recovery), so the sweep stopped to protect it. The best settings found so far are kept as the recommendation.");
                        }
                        else lighterWorked = true;
                        bestSoFar = EncoderRanking.Select(measured, settings.Goal)?.Choice;
                    }
                    // Looking better once may be noise on a link like Wi-Fi: a new best is measured a second time, and the worse of the two counts.
                    var chosen = EncoderRanking.Select(measured, settings.Goal)?.Choice ?? best;
                    while (chosen != best && confirmed.Add(chosen))
                    {
                        await _apply(chosen, token);
                        total = Math.Max(total, done + 3);
                        var label = Name(dimension, chosen) + " (confirming)";
                        var again = await Window(label);
                        if (again.MeanMbps is null) { Record(new EncoderTrial { Label = label, Choice = chosen, Step = again, Decision = "the stream was lost" }); throw Lost(chosen); }
                        measured[chosen] = Worse(measured[chosen], again);
                        Record(new EncoderTrial { Label = label, Choice = chosen, Step = again, Decision = "repeat measurement" });
                        chosen = EncoderRanking.Select(measured, settings.Goal)?.Choice ?? best;
                    }
                    best = chosen;
                    bestSoFar = best;
                    // The next option is tried on top of the best so far, so the best has to be what is running.
                    await _apply(best, token);
                }
                if (best == passStart) break;
            }

            // One last window on the combination chosen, so the recommendation has been measured as a whole.
            await _apply(best, token);
            total = Math.Max(total, done + 2);
            var final = await Window("final choice");
            Record(new EncoderTrial { Label = "Final choice", Choice = best, Step = final, Decision = final.Verdict == SweepVerdict.Clean ? "the chosen combination, measured as a whole" : "the chosen combination was not clean as a whole: run again, or at a lower bitrate", Adopted = true });
            // The user's own settings, measured the same way, so "before" and "after" are two real measurements and not a guess.
            if (best != baseline)
            {
                await _apply(baseline, token);
                var yours = await Window("your current settings");
                Record(new EncoderTrial { Label = "Your current settings", Choice = baseline, Step = yours, Decision = "for comparison" });
            }
            run.Best = best;
        }
        catch (OperationCanceledException) { run.Stopped = true; }
        catch (InvalidOperationException error)
        {
            run.Error = error.Message;
            if (run.Error.StartsWith("The stream did not settle", StringComparison.Ordinal) || run.Error.StartsWith("The stream was lost", StringComparison.Ordinal)) run.Best = bestSoFar;
        }
        if (run.Best is { } chosenBest && run.Goal != EncoderGoal.Current) Annotate(run, chosenBest, measured);
        run.Conclusion = Conclude(run);
        return run;
    }

    /// <summary>Measures the settings in use as they are: one window, nothing is changed.</summary>
    private async Task<EncoderSweepRun> RunCurrentAsync(EncoderSweepSettings settings, EncoderSweepRun run, EncoderChoice baseline, IProgress<EncoderSweepProgress>? progress, CancellationToken token)
    {
        run.Goal = EncoderGoal.Current; run.ProbeMbps = settings.ProbeMbps; run.MeasureSeconds = (int)settings.Measure.TotalSeconds; run.Baseline = baseline;
        try
        {
            run.Codec = await _measure.PrepareAsync([settings.ProbeMbps], new Progress<BenchmarkProgress>(p => { }), token);
            progress?.Report(new("waiting for the stream to settle", "start", 0, 1, 0, null));
            if (!await _measure.WaitQuietAsync(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(90), null, token))
                throw new InvalidOperationException("The stream did not settle before the measurement (it kept asking for recovery for 90 seconds). Check the link and that the headset is connected, then try again.");
            var step = await _measure.MeasureStepAsync(settings.ProbeMbps, 0, 1, TimeSpan.FromSeconds(2), settings.Measure, recheck: false,
                new Progress<BenchmarkProgress>(p => progress?.Report(new(p.Phase, "Your current settings", 0, 1, p.PhaseSecondsLeft, null))), token, waitQuiet: false);
            var trial = new EncoderTrial { Label = "Your current settings", Choice = baseline, Step = step, Adopted = true, Decision = step.MeanMbps is null ? "no video in the window" : step.Verdict switch { SweepVerdict.Clean => "clean", SweepVerdict.Marginal => "small problems", _ => "failed" } };
            run.Trials.Add(trial);
            progress?.Report(new("done", trial.Label, 1, 1, 0, trial));
            run.Best = baseline;
        }
        catch (OperationCanceledException) { run.Stopped = true; }
        catch (InvalidOperationException error) { run.Error = error.Message; }
        run.Conclusion = Conclude(run);
        return run;
    }

    /// <summary>The passes repeat until the best stops changing, at most this many.</summary>
    private const int MaxPasses = 3;

    private static BenchmarkStep NotTried(EncoderSweepSettings settings) => new(settings.ProbeMbps, 0, null, null, 0, 0, 0, 0, 0, null, null, null, 0, 0, SweepVerdict.Failed, "not tried", 1);

    /// <summary>Says, for every row, why it was or was not chosen, so the table explains the recommendation.</summary>
    private static void Annotate(EncoderSweepRun run, EncoderChoice best, IReadOnlyDictionary<EncoderChoice, BenchmarkStep> measured)
    {
        if (!measured.TryGetValue(best, out var bestStep)) return;
        var why = EncoderRanking.Select(measured, run.Goal) is { } pick && pick.Choice == best ? pick.Why : "the best found before the sweep ended";
        foreach (var trial in run.Trials)
        {
            if (trial.Label is "Final choice" or "Your current settings" || trial.Decision.StartsWith("not tried", StringComparison.Ordinal) || trial.Decision is "the stream was lost" or "repeat measurement") continue;
            if (trial.Choice == best) { trial.Decision = "chosen: " + why; trial.Adopted = true; continue; }
            trial.Adopted = false;
            if (trial.Step.Verdict == SweepVerdict.Failed) { trial.Decision = "rough: not usable"; continue; }
            var order = EncoderRanking.Compare(trial.Step, bestStep);
            if (run.Goal == EncoderGoal.Picture && EncoderRanking.Reach(bestStep) - EncoderRanking.Reach(trial.Step) > 0.05)
            { trial.Decision = $"lower picture: delivered {EncoderRanking.Reach(trial.Step) * 100:0}% of the bitrate against {EncoderRanking.Reach(bestStep) * 100:0}%"; continue; }
            trial.Decision = order > 0 ? "less smooth than the chosen"
                : order < 0 ? "smoother in this window, but within the noise of the chosen (see the repeat measurement)"
                : EncoderRanking.CostsMuchMore(trial.Step, bestStep) ? "tied on smoothness, costs much more encode time"
                : EncoderRanking.Reach(bestStep) - EncoderRanking.Reach(trial.Step) > 0.05 ? "tied on smoothness, lower reach (less of the bitrate delivered)"
                : "tied on smoothness, not picked for picture";
        }
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

    private static string ConcludeCurrent(EncoderSweepRun run)
    {
        var step = run.Trials[0].Step;
        var text = new StringBuilder($"Your current settings ({run.Baseline.Describe()}) at a {run.ProbeMbps} Mbps target, {step.Seconds:0} s: ");
        text.Append(step.MeanMbps is null ? "no video arrived. " : $"{step.Verdict.ToString().ToLowerInvariant()}. Delivered {step.MeanMbps:0} Mbps ({EncoderRanking.Reach(step) * 100:0}% of the target), ");
        if (step.MeanMbps is not null)
            text.Append($"held back {step.HeldMsPerMinute:0} ms/min ({step.HeldFrames} frames), {step.StreamResets} stream reset(s), {step.PcDropped} PC drop(s), encode {step.EncodeAverageMs?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—"} / {step.EncodeMaxMs?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—"} ms (average / slowest)" + (EncoderRanking.HasHeadroom(step) ? "" : ", the slowest encode leaves little room in a frame") + ". ");
        if (step.Verdict != SweepVerdict.Clean && step.Reason.Length > 0) text.Append("Why: " + step.Reason + " ");
        text.Append("Nothing was changed. ");
        if (run.Stopped) text.Append("The run was stopped early. ");
        if (run.Error is not null) text.Append(run.Error);
        return text.ToString().Trim();
    }

    public static string Conclude(EncoderSweepRun run)
    {
        if (run.Trials.Count == 0) return run.Error ?? "Nothing was measured.";
        if (run.Goal == EncoderGoal.Current) return ConcludeCurrent(run);
        var text = new StringBuilder();
        var measured = run.Trials.Where(t => !t.Decision.StartsWith("not tried", StringComparison.Ordinal) && t.Label != "Final choice" && t.Label != "Your current settings").ToList();
        if (run.Best is null) text.Append("The sweep ended before a recommendation could be made. ");
        else if (run.Best == run.Baseline) text.Append($"Your current settings ({run.Baseline.Describe()}) came out as the {(run.Goal == EncoderGoal.Picture ? "best picture" : "smoothest")} of those tried at {run.ProbeMbps} Mbps. ");
        else
        {
            text.Append($"{(run.Goal == EncoderGoal.Picture ? "Best picture" : "Smoothest")} at {run.ProbeMbps} Mbps: {run.Best.Describe()} (now {run.Baseline.Describe()}). ");
            var final = run.Trials.LastOrDefault(t => t.Label == "Final choice")?.Step;
            // The step measured with the user's own value of the first option, on their other options as they were, is the nearest thing to "before".
            var before = run.Trials.FirstOrDefault(t => t.Label == "Your current settings")?.Step ?? measured.FirstOrDefault(t => t.Choice == run.Baseline)?.Step;
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

    /// <summary>Removes a saved sweep (its json and csv).</summary>
    public static void Delete(string directory, EncoderSweepRun run) => ReportFiles.Delete(directory, "encoder-sweep-", run.At);

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
        text.AppendLine("candidate,rate_control,preset,vbv_frames,multipass,seconds,mean_mbps,held_frames,held_ms_per_minute,pc_dropped,stream_resets,encode_avg_ms,encode_max_ms,verdict,decision,reason");
        string N(double? v, string f = "0.0") => v is double d && double.IsFinite(d) ? d.ToString(f, CultureInfo.InvariantCulture) : "";
        foreach (var t in run.Trials)
            text.AppendLine(string.Join(',', new[]
            {
                "\"" + t.Label.Replace("\"", "\"\"") + "\"", t.Choice.RateControl, t.Choice.Preset.ToString(CultureInfo.InvariantCulture), t.Choice.VbvFrames.ToString(CultureInfo.InvariantCulture), t.Choice.MultiPass.ToString(CultureInfo.InvariantCulture),
                N(t.Step.Seconds), N(t.Step.MeanMbps), t.Step.HeldFrames.ToString(CultureInfo.InvariantCulture), N(t.Step.HeldMsPerMinute, "0"), t.Step.PcDropped.ToString(CultureInfo.InvariantCulture),
                t.Step.StreamResets.ToString(CultureInfo.InvariantCulture), N(t.Step.EncodeAverageMs, "0.00"), N(t.Step.EncodeMaxMs, "0.00"), t.Step.Verdict.ToString(), "\"" + t.Decision + "\"", "\"" + t.Step.Reason.Replace("\"", "\"\"") + "\""
            }));
        return text.ToString();
    }
}
