using System.Drawing;
using CodecSwapper;
using SteamFrame.Core;

namespace CodecSwapperTests;

public sealed class ChartDrawTests
{
    [Fact]
    public void Draws_both_chart_kinds_without_throwing()
    {
        static BenchmarkStep S(int t, double mbps, double held, double enc, SweepVerdict v) => new(t, 24, mbps, 72, 0, held * 24 / 60, 0, 0, 0, enc, enc + 1, null, 0, 0, v, "", 1);
        var a = new BenchmarkRun { At = DateTimeOffset.Now, Codec = "HEVC", Steps = [S(100, 95, 0, 9.7, SweepVerdict.Clean), S(200, 182, 0, 9.7, SweepVerdict.Clean), S(300, 255, 30, 9.7, SweepVerdict.Clean), S(400, 258, 300, 9.7, SweepVerdict.Marginal), S(500, 270, 900, 9.7, SweepVerdict.Failed)] };
        var b = new BenchmarkRun { At = DateTimeOffset.Now, Codec = "H.264", Steps = [S(100, 99, 0, 3.8, SweepVerdict.Clean), S(200, 199, 0, 3.8, SweepVerdict.Clean), S(300, 301, 0, 3.8, SweepVerdict.Clean), S(400, 392, 120, 3.8, SweepVerdict.Failed)] };
        using var chart = new RunChart { Size = new Size(1000, 560) };
        chart.Show([new ChartSeries("Nov 3 22:10 HEVC (clean to 400)", ChartData.Palette[0], ChartData.Of(a)), new ChartSeries("Nov 3 21:40 H.264", ChartData.Palette[1], ChartData.Of(b))], bars: false);
        using var bmp = new Bitmap(1000, 560);
        chart.DrawToBitmap(bmp, new Rectangle(0, 0, 1000, 560));

        var sweep = new EncoderSweepRun { At = DateTimeOffset.Now, Codec = "HEVC", Trials = [] };
        string[] names = ["Current settings", "Preset P1", "Preset P4", "Preset P6", "Preset P7", "Rate control CBR", "VBV 1 frame(s)", "VBV 4 frame(s)", "Multipass 1", "Multipass 2"];
        for (var i = 0; i < names.Length; i++) sweep.Trials.Add(new EncoderTrial { Label = names[i], Step = S(200, 195 + i, i == 4 ? 600 : i * 10, 4 + i * 0.4, i == 4 ? SweepVerdict.Failed : SweepVerdict.Clean) });
        chart.Show([new ChartSeries("This run", ChartData.Palette[0], ChartData.Of(sweep))], bars: true);
        chart.DrawToBitmap(bmp, new Rectangle(0, 0, 1000, 560));
    }
}
