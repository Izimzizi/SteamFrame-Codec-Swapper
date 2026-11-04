using CodecSwapper;
using SteamFrame.Core;

namespace CodecSwapperTests;

public sealed class EncoderSweepTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sweep-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly EncoderChoice Start = new("vbr", 2, 2, 0);

    /// <summary>A one-second sample source whose behaviour depends on the encoder options last applied.</summary>
    private sealed class Rig
    {
        public EncoderChoice Choice = Start;
        public int Second;
        public long Held, Resets;
        public Func<EncoderChoice, int> ResetsPerSecond = _ => 0;
        public bool Stuck;
        public double HeldMs;
        public readonly List<EncoderChoice> Applied = [];
        public Func<EncoderChoice, int> HeldPerSecond = _ => 0;
        public Func<EncoderChoice, double> EncodeMs = c => 3 + c.Preset * 0.5;
        public Func<EncoderChoice, double> VideoMbps = _ => 200;

        public Task Apply(EncoderChoice choice, CancellationToken token) { Choice = choice; Applied.Add(choice); return Task.CompletedTask; }
        public bool StuckStorm;

        public Task<StatsSample> Next(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Second++;
            var held = HeldPerSecond(Choice);
            Held += held; HeldMs += held * 12;
            Resets += StuckStorm ? 5 : ResetsPerSecond(Choice);
            var health = new HealthReading(Held, 0, 0, true, 0, 0, 0) { HeldMsTotal = HeldMs, StreamResets = Resets };
            return Task.FromResult(new StatsSample(T0.AddSeconds(Second), VideoMbps(Choice), 72, "HEVC", true, "", true, 0, null)
            {
                Health = health, Timing = new EncodeTiming("HEVC", 72, EncodeMs(Choice), EncodeMs(Choice) + 1)
            });
        }
    }

    private static readonly EncoderSweepSettings Quick = new(200, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));

    private static Task<EncoderSweepRun> Run(Rig rig, CancellationToken token = default) =>
        new EncoderSweepRunner(new BenchmarkRunner((_, _) => Task.CompletedTask, rig.Next) { TolerableResets = 2 }, rig.Apply).RunAsync(Quick, new EncoderSweepRun { At = T0 }, Start, null, token);

    [Fact]
    public async Task Among_equally_smooth_settings_the_heavier_preset_and_multipass_and_smaller_buffer_win_but_a_rough_one_never_does()
    {
        // P7, multipass 2 and a one-frame buffer are rough on this stream; everything else is clean.
        var rig = new Rig { HeldPerSecond = c => c.Preset == 7 || c.MultiPass == 2 || c.VbvFrames == 1 ? 3 : 0 };
        var run = await Run(rig);
        Assert.Equal(new EncoderChoice("vbr", 6, 2, 1), run.Best);
        Assert.Contains("Smoothest at 200 Mbps", run.Conclusion);
        Assert.Equal(Start, run.Baseline);
        Assert.Contains(run.Trials, t => t.Label == "Preset P7" && t.Decision == "less smooth");
    }

    [Fact]
    public async Task Among_equally_smooth_buffers_the_one_that_reaches_the_target_wins_over_the_smaller_one()
    {
        // A one-frame buffer is clean but only delivers 150 of the 200 Mbps asked for; it must not win the tie on being the smallest.
        var rig = new Rig { VideoMbps = c => c.VbvFrames == 1 ? 150 : 200 };
        var run = await Run(rig);
        Assert.Equal(2, run.Best!.VbvFrames);
        Assert.Contains(run.Trials, t => t.Label.StartsWith("VBV 2", StringComparison.Ordinal) && t.Adopted);
    }

    [Fact]
    public async Task A_single_recovery_request_from_the_change_itself_does_not_fail_a_value()
    {
        var rig = new Rig { ResetsPerSecond = c => c.MultiPass == 1 && rig_Second() % 10 == 0 ? 1 : 0 };
        int rig_Second() => rigRef!.Second;
        rigRef = rig;
        var run = await Run(rig);
        var multipass1 = run.Trials.First(t => t.Label == "Multipass 1");
        Assert.NotEqual(SweepVerdict.Failed, multipass1.Step.Verdict);
        Assert.NotEqual("first value tried (rough)", multipass1.Decision);
    }

    private Rig? rigRef;

    [Fact]
    public async Task A_rough_starting_point_is_replaced_by_the_first_smooth_alternative()
    {
        var rig = new Rig { HeldPerSecond = c => c.Preset == 2 ? 4 : 0 };
        var run = await Run(rig);
        Assert.NotEqual(2, run.Best!.Preset);
        Assert.True(run.Trials.Single(t => t.Label == "Preset P2 (current)").Step.HeldMsPerMinute > 1000);
        Assert.Contains("Held-back time", run.Conclusion);
    }

    [Fact]
    public async Task When_nothing_beats_the_current_settings_they_are_kept_and_said_so()
    {
        // Every other preset, rate control, buffer and multipass is rougher than the current ones.
        var rig = new Rig { HeldPerSecond = c => c == Start ? 0 : 3 };
        var run = await Run(rig);
        Assert.Equal(Start, run.Best);
        Assert.Contains("came out as the smoothest", run.Conclusion);
    }

    [Fact]
    public async Task A_stream_that_is_clean_everywhere_says_the_bitrate_is_too_easy_to_tell_settings_apart()
    {
        var run = await Run(new Rig());
        Assert.Contains("Run it at a higher bitrate", run.Conclusion);
    }

    [Fact]
    public async Task Every_candidate_is_measured_at_the_same_bitrate_with_the_best_so_far_as_the_base()
    {
        var rig = new Rig { HeldPerSecond = c => c.Preset == 4 ? 0 : (c.Preset == 2 ? 2 : 3) };
        var run = await Run(rig);
        // After P4 won, the later candidates change one option each from P4, not from the starting P2.
        var later = run.Trials.Where(t => t.Label.StartsWith("Rate control") || t.Label.StartsWith("VBV") || t.Label.StartsWith("Multipass")).ToList();
        Assert.NotEmpty(later);
        Assert.All(later, t => Assert.Equal(run.Best!.Preset, t.Choice.Preset));
    }

    [Fact]
    public async Task Cancelling_keeps_the_trials_measured_and_makes_no_recommendation()
    {
        var rig = new Rig();
        using var cancel = new CancellationTokenSource();
        var runner = new EncoderSweepRunner(new BenchmarkRunner((_, _) => Task.CompletedTask, token => { if (rig.Second > 80) cancel.Cancel(); return rig.Next(token); }), rig.Apply);
        var run = await runner.RunAsync(Quick, new EncoderSweepRun { At = T0 }, Start, null, cancel.Token);
        Assert.True(run.Stopped);
        Assert.NotEmpty(run.Trials);
        Assert.Null(run.Best);
        Assert.Contains("ended before a recommendation", run.Conclusion);
    }

    [Fact]
    public async Task Without_SteamVR_the_sweep_says_why_and_changes_nothing()
    {
        var rig = new Rig();
        var runner = new EncoderSweepRunner(new BenchmarkRunner((_, _) => Task.CompletedTask, async token => (await rig.Next(token)) with { SteamVrRunning = false }), rig.Apply);
        var run = await runner.RunAsync(Quick, new EncoderSweepRun { At = T0 }, Start, null, default);
        Assert.Empty(rig.Applied);
        Assert.Contains("SteamVR is not running", run.Error);
    }

    [Fact]
    public async Task A_recovery_storm_cuts_the_window_short_and_heavier_settings_than_the_one_that_failed_are_not_tried()
    {
        var rig = new Rig { ResetsPerSecond = c => c.VbvFrames == 4 ? 6 : 0 };
        var run = await Run(rig);
        var four = run.Trials.Single(t => t.Label == "VBV 4 frame(s)");
        Assert.True(four.Step.Seconds < 8, "the storm window ended early");
        Assert.Equal(SweepVerdict.Failed, four.Step.Verdict);
        Assert.StartsWith("not tried", run.Trials.Single(t => t.Label == "VBV 8 frame(s)").Decision);
        Assert.NotEqual(4, run.Best!.VbvFrames);
        Assert.NotEqual(8, run.Best.VbvFrames);
    }

    [Fact]
    public async Task A_stream_that_stays_in_recovery_stops_the_sweep_and_keeps_the_best_found_so_far()
    {
        var rig = new Rig();
        var runner = new EncoderSweepRunner(new BenchmarkRunner((_, _) => Task.CompletedTask, rig.Next), async (choice, token) => { await rig.Apply(choice, token); if (choice.VbvFrames == 4) rig.StuckStorm = true; });
        var run = await runner.RunAsync(Quick, new EncoderSweepRun { At = T0 }, Start, null, default);
        Assert.Contains("did not settle", run.Error);
        Assert.NotNull(run.Best);
        Assert.DoesNotContain(run.Trials, t => t.Label.StartsWith("VBV 8"));   // the buffer is the last option tried, so nothing after VBV 4
    }

    [Fact]
    public async Task A_stream_that_never_settles_before_the_first_window_does_not_start_the_sweep()
    {
        var rig = new Rig { StuckStorm = true };
        var run = await Run(rig);
        Assert.Contains("did not settle before the first measurement", run.Error);
        Assert.Empty(run.Trials);
    }

    [Fact]
    public async Task The_progress_total_grows_with_repeat_measurements_so_the_count_never_runs_past_it()
    {
        var rig = new Rig { HeldPerSecond = c => c.Preset == 2 ? 4 : 0 };
        var reports = new List<EncoderSweepProgress>();
        var run = await new EncoderSweepRunner(new BenchmarkRunner((_, _) => Task.CompletedTask, rig.Next) { TolerableResets = 2 }, rig.Apply)
            .RunAsync(Quick, new EncoderSweepRun { At = T0 }, Start, new Progress<EncoderSweepProgress>(reports.Add), default);
        await Task.Delay(100, TestContext.Current.CancellationToken);   // Progress<T> posts asynchronously
        Assert.NotEmpty(run.Trials);
        Assert.All(reports, r => Assert.True(r.Done <= r.Total, $"{r.Done} of {r.Total}"));
    }

    [Fact]
    public void Deleting_a_saved_run_removes_its_json_and_csv()
    {
        var sweep = new EncoderSweepRun { At = T0, Codec = "H.264" };
        var path = EncoderSweepReport.Save(_dir, sweep);
        Assert.True(File.Exists(path)); Assert.True(File.Exists(Path.ChangeExtension(path, ".csv")));
        EncoderSweepReport.Delete(_dir, sweep);
        Assert.False(File.Exists(path)); Assert.False(File.Exists(Path.ChangeExtension(path, ".csv")));
        var bench = new BenchmarkRun { At = T0 };
        var benchPath = BenchmarkReport.Save(_dir, bench);
        BenchmarkReport.Delete(_dir, bench);
        Assert.False(File.Exists(benchPath));
        BenchmarkReport.Delete(_dir, bench);   // already gone is fine
    }

    private static BenchmarkStep Step(SweepVerdict verdict = SweepVerdict.Clean, double heldMsPerMinute = 0, double seconds = 24, double? encodeMax = 6, double fps = 72, long resets = 0, long drops = 0) =>
        new(200, seconds, 200, fps, 0, heldMsPerMinute * seconds / 60, drops, 0, 0, 4, encodeMax, null, resets, 0, verdict, "", 1);

    [Fact]
    public void Ranking_orders_by_verdict_then_held_time_and_ignores_differences_inside_the_noise()
    {
        Assert.True(EncoderRanking.Compare(Step(), Step(SweepVerdict.Marginal)) < 0);
        Assert.True(EncoderRanking.Compare(Step(SweepVerdict.Failed), Step(SweepVerdict.Marginal)) > 0);
        Assert.True(EncoderRanking.Compare(Step(heldMsPerMinute: 100), Step(heldMsPerMinute: 400)) < 0);
        Assert.Equal(0, EncoderRanking.Compare(Step(heldMsPerMinute: 100), Step(heldMsPerMinute: 115)));
        Assert.Equal(0, EncoderRanking.Compare(Step(heldMsPerMinute: 2000), Step(heldMsPerMinute: 2300)));
        Assert.True(EncoderRanking.Compare(Step(resets: 0), Step(resets: 4)) < 0);
        Assert.Equal(0, EncoderRanking.Compare(Step(resets: 0), Step(resets: 1)));   // one recovery request is what the change itself causes
        Assert.True(EncoderRanking.Compare(Step(), new BenchmarkStep(200, 24, 120, 72, 0, 0, 0, 0, 0, 4, 6, null, 0, 0, SweepVerdict.Clean, "", 1)) < 0);   // reaches the target vs 60% of it
    }

    [Fact]
    public void An_encoder_whose_slowest_frame_nearly_fills_the_frame_interval_loses_a_tie()
    {
        var slow = Step(encodeMax: 12);   // 72 fps -> 13.9 ms interval; 12 ms leaves no room for a game
        Assert.False(EncoderRanking.HasHeadroom(slow));
        Assert.True(EncoderRanking.Compare(Step(encodeMax: 6), slow) < 0);
    }

    [Fact]
    public void The_alternatives_of_each_option_leave_out_the_value_already_in_use()
    {
        var best = new EncoderChoice("cbr", 4, 1, 2);
        Assert.Equal(EncoderSweepSettings.Presets, EncoderSweepSettings.Values(best, EncoderDimension.Preset).Select(c => c.Preset));   // the value in use is measured like the others
        Assert.Equal(["vbr", "cbr"], EncoderSweepSettings.Values(best, EncoderDimension.RateControl).Select(c => c.RateControl));
        Assert.All(EncoderSweepSettings.Values(best, EncoderDimension.Vbv), c => Assert.Equal(("cbr", 4, 2), (c.RateControl, c.Preset, c.MultiPass)));
        Assert.Equal(14, new EncoderSweepSettings(200, TimeSpan.Zero, TimeSpan.Zero).CandidateCount);
    }

    [Fact]
    public async Task A_saved_sweep_loads_again_and_its_csv_has_a_row_per_trial()
    {
        var run = await Run(new Rig { HeldPerSecond = c => c.Preset == 7 ? 3 : 0 });
        run.Setup = ["headset kept awake by the tray"];
        EncoderSweepReport.Save(_dir, run);
        var loaded = EncoderSweepReport.LoadAll(_dir);
        Assert.Single(loaded);
        Assert.Equal(run.Trials.Count, loaded[0].Trials.Count);
        Assert.Equal(run.Best, loaded[0].Best);
        var lines = EncoderSweepReport.ToCsv(run).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(run.Trials.Count + 1, lines.Count(l => !l.StartsWith('#')));
    }

    [Fact]
    public async Task Chart_data_has_a_point_per_step_or_candidate_in_the_order_measured()
    {
        var sweep = await Run(new Rig());
        var points = ChartData.Of(sweep);
        Assert.Equal(sweep.Trials.Count(t => t.Label != "Final choice" && t.Decision != "final check"), points.Count);
        Assert.Equal("P1", points[0].Label);
        Assert.Equal("P2*", points[1].Label);   // the value in use is marked
        Assert.Equal(Enumerable.Range(0, points.Count).Select(i => (double)i), points.Select(p => p.X));
        var bitrate = new BenchmarkRun { Steps = [Step() with { TargetMbps = 100 }, Step() with { TargetMbps = 150 }] };
        Assert.Equal([100.0, 150.0], ChartData.Of(bitrate).Select(p => p.X));
    }
}
