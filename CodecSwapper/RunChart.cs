using System.Drawing.Drawing2D;
using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>One measured point of a run: a bitrate step, or one encoder candidate.</summary>
public sealed record ChartPoint(double X, string Label, double? Delivered, double HeldMsPerMinute, double? EncodeMs, double PcDropsPerMinute, SweepVerdict Verdict);

public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<ChartPoint> Points);

public static class ChartData
{
    private static ChartPoint Point(double x, string label, BenchmarkStep s) =>
        new(x, label, s.MeanMbps, s.HeldMsPerMinute, s.EncodeAverageMs, s.Seconds > 0 ? s.PcDropped * 60.0 / s.Seconds : 0, s.Verdict);

    /// <summary>A bitrate run as points along the target axis.</summary>
    public static IReadOnlyList<ChartPoint> Of(BenchmarkRun run) => run.Steps.Select(s => Point(s.TargetMbps, s.TargetMbps + " Mbps", s)).ToList();

    /// <summary>An encoder sweep as one point per candidate, in the order they were tried (the final checks are left out).</summary>
    public static IReadOnlyList<ChartPoint> Of(EncoderSweepRun run) =>
        run.Trials.Where(t => t.Decision != "final check" && t.Label != "Final choice" && !t.Decision.StartsWith("not tried", StringComparison.Ordinal)).Select((t, i) => Point(i, ShortLabel(t), t.Step)).ToList();

    public static string ShortLabel(EncoderTrial trial) => trial.Label.Replace(" (current)", "*").Replace("Preset ", "").Replace("Rate control ", "").Replace(" frame(s)", "").Replace("Multipass ", "MP ");

    /// <summary>Okabe-Ito colours: distinguishable with the common kinds of colour blindness, and readable on light and dark backgrounds.</summary>
    public static readonly Color[] Palette =
    [
        Color.FromArgb(0, 114, 178), Color.FromArgb(230, 159, 0), Color.FromArgb(0, 158, 115),
        Color.FromArgb(204, 121, 167), Color.FromArgb(86, 180, 233), Color.FromArgb(213, 94, 0),
    ];
}

/// <summary>
/// Four small charts that read the same way: delivered rate, held-back time, encode time and PC drops, one line per run (or one bar group per candidate).
/// Hovering names the values at that point. It draws only; the form decides which runs are shown.
/// </summary>
public sealed class RunChart : Control
{
    private List<ChartSeries> _series = [];
    private bool _bars;
    private readonly ToolTip _tip = new();
    private readonly List<(RectangleF Area, int Metric)> _panels = [];
    private string _lastTip = "";

    private static readonly string[] Titles = ["Delivered rate (Mbps)", "Held-back time (ms per minute) — lower is smoother", "Encode time (ms, average)", "PC dropped frames per minute"];

    public RunChart()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        MinimumSize = new Size(400, 300);
    }

    /// <param name="bars">Candidates along the axis as grouped bars (encoder sweeps); otherwise lines against the target bitrate.</param>
    public void Show(IEnumerable<ChartSeries> series, bool bars)
    {
        _series = series.ToList();
        _bars = bars;
        Invalidate();
    }

    private static double? Metric(ChartPoint p, int metric) => metric switch { 0 => p.Delivered, 1 => p.HeldMsPerMinute, 2 => p.EncodeMs, _ => p.PcDropsPerMinute };

    private static double NiceMax(double value)
    {
        if (value <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1.0, 2, 2.5, 5, 10 }) if (value <= step * magnitude) return step * magnitude;
        return 10 * magnitude;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        _panels.Clear();
        var scale = DeviceDpi / 96f;
        if (_series.Count == 0 || _series.All(s => s.Points.Count == 0))
        {
            using var brush = new SolidBrush(ForeColor);
            g.DrawString("Nothing to show yet. Run a benchmark, or tick saved runs in the list.", Font, brush, 12, 12);
            return;
        }
        // Legend across the top.
        var x = 8f;
        var y = 4f;
        foreach (var series in _series)
        {
            var width = g.MeasureString(series.Name, Font).Width + 26 * scale;
            if (x + width > Width - 8 && x > 8) { x = 8; y += Font.Height + 4; }
            using (var swatch = new SolidBrush(series.Color)) g.FillRectangle(swatch, x, y + 3, 14 * scale, Font.Height - 6);
            using (var text = new SolidBrush(ForeColor)) g.DrawString(series.Name, Font, text, x + 18 * scale, y);
            x += width + 12;
        }
        var top = y + Font.Height + 10;
        var cellWidth = (Width - 12) / 2f;
        var cellHeight = (Height - top - 6) / 2f;
        for (var metric = 0; metric < 4; metric++)
        {
            var area = new RectangleF(6 + metric % 2 * cellWidth, top + metric / 2 * cellHeight, cellWidth - 8, cellHeight - 6);
            _panels.Add((area, metric));
            DrawPanel(g, area, metric, scale);
        }
    }

    private (float Left, float Right, float Top, float Bottom) Plot(RectangleF area, float scale) =>
        (area.Left + 50 * scale, area.Right - 10 * scale, area.Top + Font.Height + 6, area.Bottom - (_bars ? 54 * scale : Font.Height + 8));

    private List<double> Axis() => _bars ? Enumerable.Range(0, _series.Max(s => s.Points.Count)).Select(i => (double)i).ToList()
        : _series.SelectMany(s => s.Points.Select(p => p.X)).Distinct().OrderBy(v => v).ToList();

    private void DrawPanel(Graphics g, RectangleF area, int metric, float scale)
    {
        using var text = new SolidBrush(ForeColor);
        using var faint = new SolidBrush(Color.FromArgb(150, ForeColor));
        using var grid = new Pen(Color.FromArgb(48, ForeColor));
        using var axis = new Pen(Color.FromArgb(120, ForeColor));
        g.DrawString(Titles[metric], Font, text, area.Left + 4, area.Top);
        var (left, right, top, bottom) = Plot(area, scale);
        var xs = Axis();
        var values = _series.SelectMany(s => s.Points).Select(p => Metric(p, metric)).Where(v => v is not null).Select(v => v!.Value).ToList();
        var peak = values.Count == 0 ? 1 : values.Max();
        if (metric == 0) peak = Math.Max(peak, xs.Count == 0 || _bars ? 0 : xs.Max());
        var ymax = NiceMax(peak * 1.05);
        for (var i = 0; i <= 5; i++)
        {
            var yy = bottom - (bottom - top) * i / 5f;
            g.DrawLine(grid, left, yy, right, yy);
            var label = (ymax * i / 5).ToString(ymax < 10 ? "0.0" : "0");
            var size = g.MeasureString(label, Font);
            g.DrawString(label, Font, faint, left - size.Width - 2, yy - size.Height / 2);
        }
        g.DrawLine(axis, left, bottom, right, bottom);
        float XOf(double value, int index)
        {
            if (_bars) return left + (right - left) * (index + 0.5f) / Math.Max(1, xs.Count);
            if (xs.Count < 2) return (left + right) / 2;
            return left + (float)((value - xs[0]) / (xs[^1] - xs[0])) * (right - left);
        }
        float YOf(double value) => bottom - (float)(Math.Min(value, ymax) / ymax) * (bottom - top);

        // Axis labels: every bitrate step (thinned to fit), or every candidate on a slant.
        var labelled = 0f;
        for (var i = 0; i < xs.Count; i++)
        {
            var label = _bars ? _series.Select(s => i < s.Points.Count ? s.Points[i].Label : null).FirstOrDefault(l => l is not null) ?? "" : xs[i].ToString("0");
            var px = XOf(xs[i], i);
            if (_bars)
            {
                var state = g.Save();
                g.TranslateTransform(px, bottom + 4);
                g.RotateTransform(45);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(label, Font, faint, 0, 0);
                g.Restore(state);
            }
            else
            {
                var size = g.MeasureString(label, Font);
                if (px - size.Width / 2 < labelled) continue;
                g.DrawString(label, Font, faint, px - size.Width / 2, bottom + 3);
                labelled = px + size.Width / 2 + 4;
            }
        }
        if (metric == 0 && !_bars && xs.Count >= 2)
        {
            // What a perfect stream would deliver: the target itself.
            using var ideal = new Pen(Color.FromArgb(120, ForeColor)) { DashStyle = DashStyle.Dash };
            g.DrawLine(ideal, XOf(xs[0], 0), YOf(xs[0]), XOf(xs[^1], xs.Count - 1), YOf(xs[^1]));
        }
        var count = _series.Count;
        var group = (right - left) / Math.Max(1, xs.Count);
        for (var s = 0; s < count; s++)
        {
            var series = _series[s];
            using var pen = new Pen(series.Color, 2.2f * scale) { LineJoin = LineJoin.Round };
            using var fill = new SolidBrush(series.Color);
            var line = new List<PointF>();
            for (var i = 0; i < series.Points.Count; i++)
            {
                var point = series.Points[i];
                if (Metric(point, metric) is not double value) continue;
                var index = _bars ? i : xs.IndexOf(point.X);
                var px = XOf(point.X, index);
                if (_bars)
                {
                    var barWidth = Math.Max(3f, group * 0.8f / count);
                    var bx = px - group * 0.4f + s * barWidth;
                    g.FillRectangle(fill, bx, YOf(value), barWidth - 1, bottom - YOf(value));
                    if (point.Verdict != SweepVerdict.Clean)
                    {
                        using var flag = new Pen(point.Verdict == SweepVerdict.Failed ? Color.FromArgb(200, 40, 40) : Color.FromArgb(200, 140, 0), 2);
                        g.DrawRectangle(flag, bx, YOf(value), barWidth - 1, bottom - YOf(value));
                    }
                }
                else line.Add(new PointF(px, YOf(value)));
            }
            if (!_bars)
            {
                if (line.Count > 1) g.DrawLines(pen, line.ToArray());
                var marker = 4f * scale;
                var k = 0;
                for (var i = 0; i < series.Points.Count; i++)
                {
                    if (Metric(series.Points[i], metric) is null) continue;
                    var at = line[k++];
                    g.FillEllipse(fill, at.X - marker, at.Y - marker, marker * 2, marker * 2);
                    if (series.Points[i].Verdict != SweepVerdict.Clean)
                    {
                        using var flag = new Pen(series.Points[i].Verdict == SweepVerdict.Failed ? Color.FromArgb(200, 40, 40) : Color.FromArgb(200, 140, 0), 2f * scale);
                        g.DrawEllipse(flag, at.X - marker - 2, at.Y - marker - 2, marker * 2 + 4, marker * 2 + 4);
                    }
                }
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var text = "";
        foreach (var (area, metric) in _panels)
        {
            if (!area.Contains(e.Location)) continue;
            var scale = DeviceDpi / 96f;
            var (left, right, _, _) = Plot(area, scale);
            var xs = Axis();
            if (xs.Count == 0 || e.X < left - 4 || e.X > right + 4) break;
            int nearest;
            if (_bars) nearest = Math.Clamp((int)((e.X - left) / Math.Max(1, (right - left) / xs.Count)), 0, xs.Count - 1);
            else if (xs.Count < 2) nearest = 0;
            else nearest = Enumerable.Range(0, xs.Count).OrderBy(i => Math.Abs(left + (float)((xs[i] - xs[0]) / (xs[^1] - xs[0])) * (right - left) - e.X)).First();
            var lines = new List<string>();
            foreach (var series in _series)
            {
                var point = _bars ? (nearest < series.Points.Count ? series.Points[nearest] : null) : series.Points.FirstOrDefault(p => p.X == xs[nearest]);
                if (point is null) continue;
                var value = Metric(point, metric);
                lines.Add($"{series.Name}: {(value is double v ? v.ToString("0.0") : "—")}   ({point.Verdict}: held {point.HeldMsPerMinute:0} ms/min, encode {(point.EncodeMs is double ms ? ms.ToString("0.0") : "—")} ms)");
            }
            if (lines.Count > 0) text = Titles[metric].Split(" — ")[0] + " at " + (_bars ? _series.SelectMany(s => s.Points).FirstOrDefault(p => (int)p.X == nearest)?.Label : xs[nearest].ToString("0") + " Mbps") + "\n" + string.Join("\n", lines);
            break;
        }
        if (text != _lastTip) { _lastTip = text; if (text.Length == 0) _tip.Hide(this); else _tip.Show(text, this, e.X + 14, e.Y + 18, 8000); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _lastTip = "";
        _tip.Hide(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
