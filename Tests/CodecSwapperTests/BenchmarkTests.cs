using CodecSwapper;
using SteamFrame.Core;

namespace CodecSwapperTests;

public sealed class BenchmarkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "benchmark-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A one-second sample source whose behaviour depends on the target last applied.</summary>
    private sealed class Rig
    {
        public int Target;
        public int Second;
        public readonly List<int> Applied = [];
        public long Held, Dropped, Resets;
        public double HeldMs;
        public Func<int, (long HeldPerSecond, long DroppedPerSecond)> Trouble = _ => (0, 0);
        public bool SteamVrRunning = true;
        public Func<int, double?> Mbps = target => target * 0.9;

        public Task Apply(int mbps, CancellationToken token) { Target = mbps; Applied.Add(mbps); return Task.CompletedTask; }

        public Task<StatsSample> Next(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Second++;
            var (held, dropped) = Trouble(Target);
            Held += held; Dropped += dropped; HeldMs += held * 12;
            var health = new HealthReading(Held, 0, 0, true, Dropped, 0, 0) { HeldMsTotal = HeldMs, StreamResets = Resets };
            return Task.FromResult(new StatsSample(T0.AddSeconds(Second), Mbps(Target), 72, "H.264", true, "", SteamVrRunning, 0, null)
            {
                Health = health, Timing = new EncodeTiming("H.264", 72, 4 + Target / 100.0, 9)
            });
        }
    }

    private static BenchmarkSettings Quick(int start = 100, int ceiling = 300, int step = 100, bool stop = true) =>
        new(start, ceiling, step, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), stop);

    private static Task<BenchmarkRun> Run(Rig rig, BenchmarkSettings settings, CancellationToken token = default) =>
        new BenchmarkRunner(rig.Apply, rig.Next).RunAsync(settings, new BenchmarkRun { At = T0 }, null, token);

    [Fact]
    public async Task Every_target_is_applied_in_order_and_measured_over_its_own_window()
    {
        var rig = new Rig();
        var run = await Run(rig, Quick());
        Assert.Equal([100, 200, 300], rig.Applied);
        Assert.Equal([100, 200, 300], run.Steps.Select(s => s.TargetMbps));
        Assert.All(run.Steps, s => Assert.Equal(SweepVerdict.Clean, s.Verdict));
        Assert.Equal(300, run.BestCleanMbps);
        Assert.All(run.Steps, s => Assert.InRange(s.Seconds, 9, 11));
        Assert.Equal(90, run.Steps[0].MeanMbps!.Value, 3);
        Assert.Equal("H.264", run.Codec);
    }

    [Fact]
    public async Task Held_frames_and_drops_are_counted_for_the_window_of_the_step_that_caused_them()
    {
        var rig = new Rig { Trouble = target => target >= 200 ? (1, 2) : (0, 0) };
        var run = await Run(rig, Quick(stop: false));
        Assert.Equal(0, run.Steps[0].HeldFrames);
        Assert.Equal(10, run.Steps[1].HeldFrames);
        Assert.Equal(20, run.Steps[1].PcDropped);
        Assert.Equal(60, run.Steps[1].HeldPerMinute, 0);
        Assert.NotEqual(SweepVerdict.Clean, run.Steps[1].Verdict);
        Assert.Equal(100, run.BestCleanMbps);
    }

    [Fact]
    public async Task A_failed_step_ends_the_run_unless_asked_to_continue()
    {
        var trouble = new Func<int, (long, long)>(target => target >= 200 ? (5, 0) : (0, 0));
        var stopping = await Run(new Rig { Trouble = trouble }, Quick(100, 400));
        Assert.Equal(200, stopping.Steps[^1].TargetMbps);
        var continuing = await Run(new Rig { Trouble = trouble }, Quick(100, 400, stop: false));
        Assert.Equal(4, continuing.Steps.Count);
    }

    [Fact]
    public async Task A_step_that_fails_once_and_is_clean_on_the_recheck_is_not_recommended_on_its_own()
    {
        var rig = new Rig();
        var seconds = 0;
        rig.Trouble = _ => ++seconds is >= 17 and <= 19 ? (6, 0) : (0, 0);   // one bad stretch inside the first measuring window only
        var run = await Run(rig, Quick(100, 100));
        Assert.Equal(2, run.Steps[0].Passes);
        Assert.Equal(SweepVerdict.Marginal, run.Steps[0].Verdict);
    }

    [Fact]
    public async Task Without_a_video_stream_the_run_says_so_and_applies_nothing()
    {
        var rig = new Rig { Mbps = target => null };
        var run = await Run(rig, Quick(100, 100));
        Assert.Empty(rig.Applied);
        Assert.Contains("No video stream", run.Error);
    }

    [Fact]
    public async Task Without_SteamVR_the_run_says_why_and_applies_nothing()
    {
        var rig = new Rig { SteamVrRunning = false };
        var run = await Run(rig, Quick());
        Assert.Empty(rig.Applied);
        Assert.Contains("SteamVR is not running", run.Error);
        Assert.Empty(run.Steps);
    }

    [Fact]
    public async Task Cancelling_keeps_the_steps_already_measured()
    {
        var rig = new Rig();
        using var cancel = new CancellationTokenSource();
        var runner = new BenchmarkRunner(rig.Apply, token => { if (rig.Applied.Count == 2 && rig.Second > 30) cancel.Cancel(); return rig.Next(token); });
        var run = await runner.RunAsync(Quick(100, 400), new BenchmarkRun { At = T0 }, null, cancel.Token);
        Assert.True(run.Stopped);
        Assert.NotEmpty(run.Steps);
        Assert.True(run.Steps.Count < 4);
        Assert.Contains("stopped early", run.Conclusion);
    }

    [Fact]
    public async Task A_stream_reset_during_the_wait_postpones_the_first_step()
    {
        var rig = new Rig();
        var seconds = 0;
        var runner = new BenchmarkRunner(rig.Apply, token => { if (++seconds == 5) rig.Resets++; return rig.Next(token); });
        await runner.RunAsync(Quick(100, 100), new BenchmarkRun { At = T0 }, null, default);
        Assert.True(rig.Second >= 5 + 10 + 2 + 10);
    }

    [Fact]
    public void The_targets_end_on_the_ceiling_and_the_unpatched_driver_stops_at_its_own()
    {
        Assert.Equal(WirelessAdvisor.StockCeilingMbps, BenchmarkSettings.Default(false).CeilingMbps);
        Assert.Equal(WirelessAdvisor.PatchedCeilingMbps, BenchmarkSettings.Default(true).CeilingMbps);
        Assert.Equal(500, BenchmarkSettings.Default(true).Targets[^1]);
    }

    [Fact]
    public async Task A_saved_run_loads_again_and_compares_with_a_newer_one()
    {
        var older = await Run(new Rig { Trouble = t => t >= 300 ? (3, 1) : (0, 0) }, Quick(stop: false));
        older.Setup = ["rate control VBR"]; older.At = T0.AddDays(-1);
        BenchmarkReport.Save(_dir, older);
        var loaded = BenchmarkReport.LoadAll(_dir);
        Assert.Single(loaded);
        Assert.Equal(older.Steps.Count, loaded[0].Steps.Count);
        var newer = await Run(new Rig(), Quick());
        newer.Setup = ["rate control CBR"];
        var text = BenchmarkReport.Compare(newer, loaded[0]);
        Assert.Contains("Setup differs", text);
        Assert.Contains("Highest clean target: 300 Mbps now, 200 Mbps then", text);
        Assert.Contains("300 Mbps: held 0/min vs", text);
    }

    [Fact]
    public async Task The_csv_has_a_row_per_step_and_the_setup_in_comments()
    {
        var run = await Run(new Rig(), Quick());
        run.Setup = ["preset P4"];
        var lines = BenchmarkReport.ToCsv(run).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("# preset P4", lines);
        Assert.Equal(1 + 3, lines.Count(l => !l.StartsWith('#')));
    }
}
