using System.Drawing;
using System.Drawing.Imaging;
using CodecSwapper;
using SteamFrame.Core;

namespace CodecSwapperTests;

public sealed class RunSummaryTests
{
    private static BenchmarkStep S(int t, double mbps, double held, double enc, SweepVerdict v, string reason = "") => new(t, 24, mbps, 72, 0, held * 24 / 60, 0, 0, 0, enc, enc + 1, null, 0, 0, v, reason, 1);

    private static void Render(RunSummaryView view, string name)
    {
        view.Size = new Size(900, 640);
        using var bmp = new Bitmap(900, 640);
        view.DrawToBitmap(bmp, new Rectangle(0, 0, 900, 640));
        if (Environment.GetEnvironmentVariable("SUMMARY_PNG_DIR") is { Length: > 0 } dir) bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
    }

    [Fact]
    public void The_summary_draws_bitrate_runs_and_sweeps_without_throwing()
    {
        var a = new BenchmarkRun { At = DateTimeOffset.Now, Codec = "H.264", BestCleanMbps = 300, Steps = [S(100, 99, 0, 4.1, SweepVerdict.Clean), S(200, 197, 0, 4.4, SweepVerdict.Clean), S(300, 293, 10, 4.9, SweepVerdict.Clean), S(400, 349, 380, 5.6, SweepVerdict.Marginal), S(500, 352, 1250, 6.1, SweepVerdict.Failed, "the stream reset 6 times")] };
        var b = new BenchmarkRun { At = DateTimeOffset.Now, Codec = "HEVC", BestCleanMbps = 200, Steps = [S(100, 100, 0, 2.1, SweepVerdict.Clean), S(200, 199, 0, 2.4, SweepVerdict.Clean), S(300, 280, 90, 2.7, SweepVerdict.Marginal), S(400, 305, 900, 3.2, SweepVerdict.Failed)] };
        using var view = new RunSummaryView();
        view.Show([new SummaryRun("This run — Nov 4 14:02 H.264 1024px (clean to 300)", ChartData.Palette[0], a), new SummaryRun("Nov 3 18:40 HEVC 1344px (clean to 200)", ChartData.Palette[1], b)]);
        Render(view, "bitrate");

        var sweep = new EncoderSweepRun { At = DateTimeOffset.Now, Codec = "H.264", ProbeMbps = 200, Best = new EncoderChoice("vbr", 4, 2, 1) };
        void Add(string label, EncoderChoice c, double held, double mbps, SweepVerdict v, string decision, bool adopted = false) =>
            sweep.Trials.Add(new EncoderTrial { Label = label, Choice = c, Step = S(200, mbps, held, 4.5, v), Decision = decision, Adopted = adopted });
        Add("Preset P1", new("vbr", 1, 1, 0), 25, 190, SweepVerdict.Clean, "tied on smoothness, not picked for picture");
        Add("Preset P4", new("vbr", 4, 2, 1), 12, 196, SweepVerdict.Clean, "chosen: 3 settings were equally smooth; picked for the heavier settings", true);
        Add("Preset P7", new("vbr", 7, 1, 0), 900, 180, SweepVerdict.Failed, "rough: not usable");
        Add("Multipass 2", new("vbr", 4, 1, 2), 140, 190, SweepVerdict.Marginal, "less smooth than the chosen");
        Add("Final choice", new("vbr", 4, 2, 1), 14, 195, SweepVerdict.Clean, "the chosen combination, measured as a whole");
        Add("Your current settings", new("vbr", 2, 4, 0), 420, 160, SweepVerdict.Marginal, "for comparison");
        view.Show([new SummaryRun("This run — Nov 4 14:30 H.264 at 200 Mbps", ChartData.Palette[0], sweep)]);
        Render(view, "sweep");
    }
}
