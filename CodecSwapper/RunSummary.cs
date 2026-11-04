using System.Drawing.Drawing2D;
using System.Globalization;
using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>A ticked run in the summary: the name from the list, its colour, and the run itself.</summary>
public sealed record SummaryRun(string Name, Color Color, object Run);

/// <summary>
/// The answers first: for bitrate runs a scorecard (one row per run, the first is the reference, with the difference to it) and a verdict ribbon
/// (how far each run got); for encoder sweeps a ranked leaderboard (the chosen settings and why) and a before/after for the choice against the settings in use.
/// </summary>
public sealed class RunSummaryView : ScrollableControl
{
    private static readonly Color Good = Color.FromArgb(30, 130, 60), Warn = Color.FromArgb(200, 140, 0), Bad = Color.FromArgb(190, 40, 40);
    private static readonly StringFormat Format = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };

    private List<SummaryRun> _runs = [];
    private readonly ToolTip _tip = new() { AutoPopDelay = 30000, InitialDelay = 150 };
    private readonly List<(RectangleF Area, string Text)> _hits = [];
    private string _lastTip = "";
    private int _contentHeight;

    public RunSummaryView()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        AutoScroll = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    public void Show(IEnumerable<SummaryRun> runs)
    {
        _runs = runs.ToList();
        Invalidate();
    }

    // ----- painting -----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        _hits.Clear();
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        var height = Draw(g, Math.Max(360, ClientSize.Width - 20));
        g.ResetTransform();
        if (height != _contentHeight && IsHandleCreated)
        {
            _contentHeight = height;
            BeginInvoke(() => { if (!IsDisposed) AutoScrollMinSize = new Size(0, height); });
        }
    }

    private int Draw(Graphics g, int width)
    {
        if (_runs.Count == 0) { Note(g, "Nothing to show yet. Run a benchmark, or tick saved runs in the list.", 12, 12, width); return 40; }
        if (_runs.All(r => r.Run is BenchmarkRun)) return DrawBitrate(g, width);
        if (_runs.All(r => r.Run is EncoderSweepRun)) return DrawSweeps(g, width);
        Note(g, "Tick runs of one kind to compare them.", 12, 12, width);
        return 40;
    }

    private Brush Ink => new SolidBrush(ForeColor);
    private Brush Faint => new SolidBrush(Color.FromArgb(140, ForeColor));

    private float Measure(Graphics g, string text, Font font) => g.MeasureString(text, font, 10000, Format).Width;

    private void DrawText(Graphics g, string text, Font font, Color color, float x, float y)
    {
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, x, y, Format);
    }

    private string Fit(Graphics g, string text, Font font, float width)
    {
        if (Measure(g, text, font) <= width) return text;
        while (text.Length > 1 && Measure(g, text + "…", font) > width) text = text[..^1];
        return text + "…";
    }

    private void Note(Graphics g, string text, float x, float y, float width)
    {
        using var brush = Faint;
        g.DrawString(text, Font, brush, new RectangleF(x, y, width - x, 200));
    }

    private void Heading(Graphics g, string title, string? sub, float y, float width)
    {
        using var bold = new Font(Font, FontStyle.Bold);
        DrawText(g, title, bold, ForeColor, 6, y);
        if (sub is not null) DrawText(g, sub, Font, Color.FromArgb(140, ForeColor), 6 + Measure(g, title, bold) + 10, y);
    }

    private static Color VerdictColor(SweepVerdict verdict) => verdict switch { SweepVerdict.Clean => Good, SweepVerdict.Marginal => Warn, _ => Bad };

    private static string F(double value, string format = "0") => value.ToString(format, CultureInfo.InvariantCulture);

    // ----- bitrate runs -----

    private int DrawBitrate(Graphics g, int width)
    {
        var runs = _runs.Where(r => r.Run is BenchmarkRun { Steps.Count: > 0 }).Select(r => (r.Name, r.Color, Run: (BenchmarkRun)r.Run)).ToList();
        if (runs.Count == 0) { Note(g, "The ticked runs have no steps yet.", 12, 12, width); return 40; }
        var common = new HashSet<int>(runs[0].Run.Steps.Select(s => s.TargetMbps));
        foreach (var run in runs.Skip(1)) common.IntersectWith(run.Run.Steps.Select(s => s.TargetMbps));
        var reference = runs[0].Run;
        int? at = null;
        if (common.Count > 0)
        {
            var clean = common.Where(t => reference.Steps.First(s => s.TargetMbps == t).Verdict == SweepVerdict.Clean).ToList();
            at = clean.Count > 0 ? clean.Max() : common.Max();
        }
        var y = 6f;
        Heading(g, "Scorecard", at is int t0 ? $"numbers at {t0} Mbps, compared with the first run (the reference)" : "the runs have no bitrate step in common, so only how far each got is compared", y, width);
        y += Font.Height + 8;

        var nameWidth = Math.Min(260f, width * 0.32f);
        var columns = new[] { "Clean up to", "Delivered", "Held-back ms/min", "Encode ms", "PC drops/min", "Verdict" };
        var colWidth = Math.Max(96f, (width - nameWidth - 12) / columns.Length);
        for (var c = 0; c < columns.Length; c++)
            if (at is not null || c == 0) DrawText(g, columns[c], Font, Color.FromArgb(140, ForeColor), 6 + nameWidth + c * colWidth, y);
        y += Font.Height + 4;
        using (var line = new Pen(Color.FromArgb(50, ForeColor))) g.DrawLine(line, 6, y, width, y);
        y += 4;

        BenchmarkStep? StepAt(BenchmarkRun run) => at is int t ? run.Steps.FirstOrDefault(s => s.TargetMbps == t) : null;
        double Drops(BenchmarkStep s) => s.Seconds > 0 ? s.PcDropped * 60.0 / s.Seconds : 0;
        var bestClean = runs.Max(r => r.Run.BestCleanMbps ?? 0);
        var bestDelivered = runs.Select(r => StepAt(r.Run)?.MeanMbps ?? 0).Max();
        var bestHeld = runs.Select(r => StepAt(r.Run)?.HeldMsPerMinute ?? double.MaxValue).Min();
        var bestEncode = runs.Select(r => StepAt(r.Run)?.EncodeAverageMs ?? double.MaxValue).Min();
        var bestDrops = runs.Select(r => StepAt(r.Run) is { } s ? Drops(s) : double.MaxValue).Min();
        var rowHeight = Font.Height * 2 + 12f;
        using var bold = new Font(Font, FontStyle.Bold);

        for (var i = 0; i < runs.Count; i++)
        {
            var (name, color, run) = runs[i];
            var step = StepAt(run);
            var refStep = StepAt(reference);
            using (var swatch = new SolidBrush(color)) g.FillRectangle(swatch, 6, y + 4, 5, rowHeight - 8);
            DrawText(g, Fit(g, name, i == 0 ? bold : Font, nameWidth - 20), i == 0 ? bold : Font, ForeColor, 16, y + (rowHeight - Font.Height) / 2);
            _hits.Add((new RectangleF(6, y, nameWidth, rowHeight), name + (i == 0 ? "\n(the reference run)" : "")));

            var x = 6 + nameWidth;
            var clean = run.BestCleanMbps ?? 0;
            using (var bar = new SolidBrush(color)) g.FillRectangle(bar, x, y + 5 + Font.Height / 2f - 3, Math.Max(2, clean / 500f * (colWidth - 56)), 6);
            Cell(g, F(clean) + " Mbps", clean == bestClean, x + 0, y, null, null, colWidth, bold, shift: clean / 500f * (colWidth - 56) + 4);
            x += colWidth;
            if (step is not null)
            {
                var deliveredText = step.MeanMbps is double d ? F(d) + " Mbps" : "—";
                Cell(g, deliveredText, step.MeanMbps == bestDelivered, x, y, i == 0 ? null : Delta((step.MeanMbps ?? 0) - (refStep?.MeanMbps ?? 0), 1, false, " Mbps", "0"), null, colWidth, bold);
                x += colWidth;
                Cell(g, F(step.HeldMsPerMinute), Math.Abs(step.HeldMsPerMinute - bestHeld) < 0.5, x, y, i == 0 ? null : Delta(step.HeldMsPerMinute - (refStep?.HeldMsPerMinute ?? 0), 1, true, "", "0"), null, colWidth, bold);
                x += colWidth;
                Cell(g, step.EncodeAverageMs is double en ? F(en, "0.0") : "—", step.EncodeAverageMs is double e2 && Math.Abs(e2 - bestEncode) < 0.05, x, y, i == 0 || refStep?.EncodeAverageMs is null || step.EncodeAverageMs is null ? null : Delta(step.EncodeAverageMs.Value - refStep.EncodeAverageMs.Value, 0.05, true, "", "0.0"), null, colWidth, bold);
                x += colWidth;
                Cell(g, F(Drops(step), "0.#"), Math.Abs(Drops(step) - bestDrops) < 0.05, x, y, i == 0 ? null : Delta(Drops(step) - (refStep is null ? 0 : Drops(refStep)), 0.5, true, "", "0"), null, colWidth, bold);
                x += colWidth;
                var chip = step.Verdict.ToString();
                var chipWidth = Measure(g, chip, Font) + 14;
                using (var fill = new SolidBrush(Color.FromArgb(40, VerdictColor(step.Verdict)))) g.FillRectangle(fill, x, y + 4, chipWidth, Font.Height + 4);
                DrawText(g, chip, Font, VerdictColor(step.Verdict), x + 7, y + 6);
                _hits.Add((new RectangleF(x, y, chipWidth, rowHeight), $"{name}\n{at} Mbps: {step.Verdict}" + (step.Reason.Length > 0 ? "\n" + step.Reason : "")));
            }
            y += rowHeight;
            using var rowLine = new Pen(Color.FromArgb(28, ForeColor));
            g.DrawLine(rowLine, 6, y, width, y);
        }
        y += 18;

        // The ribbon: one cell per bitrate step, coloured by verdict, with how much of the target was delivered.
        Heading(g, "How far each run got", "green = clean, amber = marginal, red = failed; the number is delivered ÷ target; the dashed line follows the last clean step", y, width);
        y += Font.Height + 10;
        var targets = runs.SelectMany(r => r.Run.Steps.Select(s => s.TargetMbps)).Distinct().OrderBy(v => v).ToList();
        var labelWidth = Math.Min(230f, width * 0.28f);
        var cell = Math.Clamp((width - labelWidth - 16) / Math.Max(1, targets.Count), 34f, 90f);
        for (var i = 0; i < targets.Count; i++)
        {
            var label = F(targets[i]);
            DrawText(g, label, Font, Color.FromArgb(140, ForeColor), 6 + labelWidth + i * cell + (cell - Measure(g, label, Font)) / 2, y);
        }
        DrawText(g, "target Mbps", Font, Color.FromArgb(140, ForeColor), 6, y);
        y += Font.Height + 4;
        var ribbonRow = Font.Height + 16f;
        foreach (var (name, color, run) in runs)
        {
            using (var swatch = new SolidBrush(color)) g.FillRectangle(swatch, 6, y + 4, 5, ribbonRow - 8);
            DrawText(g, Fit(g, name, Font, labelWidth - 22), Font, ForeColor, 16, y + (ribbonRow - Font.Height) / 2);
            _hits.Add((new RectangleF(6, y, labelWidth, ribbonRow), name));
            var lastClean = -1;
            for (var i = 0; i < targets.Count; i++)
            {
                var step = run.Steps.FirstOrDefault(s => s.TargetMbps == targets[i]);
                var rect = new RectangleF(6 + labelWidth + i * cell + 2, y + 2, cell - 4, ribbonRow - 4);
                if (step is null) { using var empty = new Pen(Color.FromArgb(40, ForeColor)); g.DrawRectangle(empty, rect.X, rect.Y, rect.Width, rect.Height); continue; }
                var fill = step.Verdict switch { SweepVerdict.Clean => Color.FromArgb(60, 160, 90), SweepVerdict.Marginal => Color.FromArgb(224, 165, 0), _ => Color.FromArgb(200, 60, 60) };
                using (var path = Rounded(rect, 4)) using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                var share = step.MeanMbps is double mbps && step.TargetMbps > 0 ? Math.Round(mbps / step.TargetMbps * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
                using (var bold2 = new Font(Font, FontStyle.Bold)) DrawText(g, share, bold2, Color.White, rect.X + (rect.Width - Measure(g, share, bold2)) / 2, rect.Y + (rect.Height - Font.Height) / 2);
                _hits.Add((rect, $"{name}\n{step.TargetMbps} Mbps: {step.Verdict}\ndelivered {(step.MeanMbps is double m ? F(m) : "—")} Mbps, held back {F(step.HeldMsPerMinute)} ms/min, resets {step.StreamResets}, encode {(step.EncodeAverageMs is double en ? F(en, "0.0") : "—")} ms" + (step.Reason.Length > 0 ? "\n" + step.Reason : "")));
                if (step.Verdict == SweepVerdict.Clean && lastClean == i - 1) lastClean = i;
            }
            if (lastClean >= 0)
            {
                using var dash = new Pen(ForeColor, 2f) { DashStyle = DashStyle.Dash };
                var lx = 6 + labelWidth + (lastClean + 1) * cell;
                g.DrawLine(dash, lx, y - 1, lx, y + ribbonRow + 1);
            }
            y += ribbonRow + 4;
        }
        return (int)y + 16;
    }

    /// <summary>The value and, below the first run, how it differs from the reference (green when better, red when worse).</summary>
    private void Cell(Graphics g, string value, bool best, float x, float y, (string Text, Color Color)? delta, object? unused, float width, Font bold, float shift = 0)
    {
        var font = best ? bold : Font;
        var left = x + shift;
        DrawText(g, value, font, ForeColor, left, y + 5);
        if (delta is { } d) DrawText(g, d.Text, Font, d.Color, left, y + 7 + Font.Height);
    }

    private static (string, Color)? Delta(double difference, double epsilon, bool lowerIsBetter, string unit, string format)
    {
        if (Math.Abs(difference) < epsilon) return ("same", Color.Gray);
        var better = lowerIsBetter ? difference < 0 : difference > 0;
        return ((difference > 0 ? "+" : "−") + Math.Abs(difference).ToString(format, CultureInfo.InvariantCulture) + unit, better ? Good : Bad);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ----- encoder sweeps -----

    private int DrawSweeps(Graphics g, int width)
    {
        var y = 6f;
        var shown = 0;
        foreach (var entry in _runs)
        {
            if (entry.Run is not EncoderSweepRun sweep || sweep.Trials.Count == 0) continue;
            if (shown++ >= 2) { Note(g, "Only the first two ticked sweeps are shown here.", 6, y, width); y += Font.Height + 8; break; }
            using (var swatch = new SolidBrush(entry.Color)) g.FillRectangle(swatch, 6, y + 2, 5, Font.Height + 2);
            using (var bold = new Font(Font, FontStyle.Bold)) DrawText(g, Fit(g, entry.Name, bold, width - 30), bold, ForeColor, 16, y);
            y += Font.Height + 10;
            y = sweep.Goal == EncoderGoal.Current ? DrawCurrent(g, sweep, y, width) : DrawBoard(g, sweep, y, width);
            if (sweep.Goal != EncoderGoal.Current) y = DrawBeforeAfter(g, sweep, y, width);
            y += 14;
        }
        if (shown == 0) { Note(g, "The ticked runs have no measurements yet.", 12, 12, width); return 40; }
        return (int)y + 10;
    }

    private float DrawCurrent(Graphics g, EncoderSweepRun run, float y, int width)
    {
        Note(g, run.Conclusion, 6, y, width);
        var lines = Math.Ceiling(g.MeasureString(run.Conclusion, Font, width - 12).Height / Font.Height);
        return y + (float)lines * Font.Height + 8;
    }

    private float DrawBoard(Graphics g, EncoderSweepRun run, float y, int width)
    {
        var rows = run.Trials
            .Where(t => t.Label is not ("Final choice" or "Your current settings") && !t.Decision.StartsWith("not tried", StringComparison.Ordinal) && t.Decision is not ("repeat measurement" or "the stream was lost"))
            .GroupBy(t => t.Choice).Select(grp => grp.First()).ToList();
        int Class(EncoderTrial t) => t.Step.Verdict switch { SweepVerdict.Clean => 0, SweepVerdict.Marginal => 1, _ => 2 };
        var chosen = rows.Where(t => t.Choice == run.Best).ToList();
        var rest = rows.Where(t => t.Choice != run.Best);
        var ordered = chosen.Concat(run.Goal == EncoderGoal.Picture
            ? rest.OrderBy(Class).ThenByDescending(t => Math.Round(EncoderRanking.Reach(t.Step) / 0.05)).ThenBy(t => t.Step.HeldMsPerMinute)
            : rest.OrderBy(Class).ThenBy(t => t.Step.HeldMsPerMinute).ThenByDescending(t => EncoderRanking.Reach(t.Step))).ToList();

        Heading(g, run.Goal == EncoderGoal.Picture ? "Ranked by picture" : "Ranked by smoothness", "bar = held-back ms per minute (short is smooth), tick = share of the bitrate delivered", y, width);
        y += Font.Height + 8;
        const int limit = 12;
        var labelWidth = Math.Min(300f, width * 0.34f);
        var noteWidth = Math.Max(150f, width * 0.34f);
        var barWidth = Math.Max(100f, width - labelWidth - noteWidth - 30);
        noteWidth = Math.Max(150f, Math.Min(noteWidth, width - labelWidth - barWidth - 24));
        var worstGood = ordered.Where(t => t.Step.Verdict != SweepVerdict.Failed).Select(t => t.Step.HeldMsPerMinute).DefaultIfEmpty(10).Max();
        var scaleMax = Math.Max(60, worstGood * 1.3);
        using var bold = new Font(Font, FontStyle.Bold);
        for (var i = 0; i < Math.Min(limit, ordered.Count); i++)
        {
            var trial = ordered[i];
            var best = trial.Choice == run.Best;
            var noteHeight = g.MeasureString(trial.Decision, best ? bold : Font, (int)noteWidth).Height;
            var rowHeight = Math.Max(Font.Height + 12f, noteHeight + 10f);
            if (best) { using var back = new SolidBrush(Color.FromArgb(34, 0, 114, 178)); g.FillRectangle(back, 2, y, width - 4, rowHeight); }
            var font = best ? bold : Font;
            DrawText(g, Fit(g, trial.Choice.Describe(), font, labelWidth - 10), font, ForeColor, 8, y + 5);
            var color = VerdictColor(trial.Step.Verdict);
            var w = (float)(Math.Min(trial.Step.HeldMsPerMinute, scaleMax) / scaleMax * (barWidth - 40));
            using (var fill = new SolidBrush(color)) g.FillRectangle(fill, 8 + labelWidth, y + rowHeight / 2 - 6, Math.Max(3, w), 12);
            var valueText = F(trial.Step.HeldMsPerMinute);
            DrawText(g, valueText, Font, ForeColor, 8 + labelWidth + Math.Max(3, w) + 4, y + 5);
            var tickX = 8 + labelWidth + (float)EncoderRanking.Reach(trial.Step) * barWidth;
            using (var tick = new Pen(Color.FromArgb(200, ForeColor), 2)) g.DrawLine(tick, tickX, y + 3, tickX, y + rowHeight - 3);
            using (var noteBrush = new SolidBrush(best ? ForeColor : Color.FromArgb(150, ForeColor))) g.DrawString(trial.Decision, best ? bold : Font, noteBrush, new RectangleF(8 + labelWidth + barWidth + 14, y + 5, noteWidth, rowHeight));
            _hits.Add((new RectangleF(2, y, width - 4, rowHeight), $"{trial.Choice.Describe()}\n{trial.Step.Verdict}: held back {F(trial.Step.HeldMsPerMinute)} ms/min, delivered {(trial.Step.MeanMbps is double m ? F(m) : "—")} Mbps ({F(EncoderRanking.Reach(trial.Step) * 100)}% of the target), resets {trial.Step.StreamResets}, encode {(trial.Step.EncodeAverageMs is double en ? F(en, "0.0") : "—")} ms\n{trial.Decision}" + (trial.Step.Reason.Length > 0 && trial.Step.Verdict == SweepVerdict.Failed ? "\n" + trial.Step.Reason : "")));
            y += rowHeight;
        }
        if (ordered.Count > limit) { Note(g, $"{ordered.Count - limit} more settings in the table.", 8, y + 2, width); y += Font.Height + 6; }
        return y + 12;
    }

    private float DrawBeforeAfter(Graphics g, EncoderSweepRun run, float y, int width)
    {
        var before = run.Trials.LastOrDefault(t => t.Label == "Your current settings")?.Step;
        var after = run.Trials.LastOrDefault(t => t.Label == "Final choice")?.Step;
        if (before is null || after is null)
        {
            Note(g, "No before and after: the chosen settings are what you use now, or the run ended before they were compared.", 6, y, width);
            return y + Font.Height * 2 + 6;
        }
        Heading(g, "Your settings and the recommendation", null, y, width);
        y += Font.Height + 6;
        double Resets(BenchmarkStep s) => s.Seconds > 0 ? s.StreamResets * 60.0 / s.Seconds : 0;
        string Change(double from, double to, string less, string more) => from <= 0.0001 ? (to <= 0.0001 ? "" : $"{more}") : Math.Abs((to - from) / from) < 0.03 ? "about the same" : $"{Math.Abs((to - from) / from * 100):0}% {(to < from ? less : more)}";
        var parts = new List<string>();
        if (before.HeldMsPerMinute > 0 || after.HeldMsPerMinute > 0) parts.Add("holds frames back " + (Change(before.HeldMsPerMinute, after.HeldMsPerMinute, "less", "more") is { Length: > 0 } h ? h : "more"));
        if (before.MeanMbps is double b && after.MeanMbps is double a && b > 0) parts.Add("delivers " + Change(b, a, "less", "more") + " of the stream");
        if (Resets(before) > 0 || Resets(after) > 0) parts.Add($"resets {F(Resets(before), "0.#")} → {F(Resets(after), "0.#")} per minute");
        var headline = "Compared with your current settings, the recommendation " + string.Join(", ", parts) + (before.EncodeAverageMs is double eb && after.EncodeAverageMs is double ea ? $"; encode time {F(eb, "0.0")} → {F(ea, "0.0")} ms." : ".");
        using (var brush = new SolidBrush(ForeColor)) g.DrawString(headline, Font, brush, new RectangleF(6, y, width - 12, 90));
        y += (float)Math.Ceiling(g.MeasureString(headline, Font, width - 12).Height) + 8;

        var items = new List<(string Name, string Unit, double From, double To, bool LowerIsBetter, string Format)>
        {
            ("Held-back time", "ms/min", before.HeldMsPerMinute, after.HeldMsPerMinute, true, "0"),
            ("Delivered", "Mbps", before.MeanMbps ?? 0, after.MeanMbps ?? 0, false, "0"),
            ("Stream resets", "/min", Resets(before), Resets(after), true, "0.#"),
            ("Encode time", "ms", before.EncodeAverageMs ?? 0, after.EncodeAverageMs ?? 0, true, "0.0"),
        };
        var labelWidth = 120f;
        var right = Math.Min(210f, width * 0.3f);
        var track = Math.Max(120f, width - labelWidth - right - 30);
        // Legend
        using (var ring = new Pen(Color.Gray, 2)) g.DrawEllipse(ring, 8 + labelWidth, y + 2, 10, 10);
        DrawText(g, "your settings", Font, Color.FromArgb(140, ForeColor), 8 + labelWidth + 14, y);
        using (var dot = new SolidBrush(ForeColor)) g.FillEllipse(dot, 8 + labelWidth + 120, y + 2, 10, 10);
        DrawText(g, "recommended", Font, Color.FromArgb(140, ForeColor), 8 + labelWidth + 134, y);
        y += Font.Height + 8;
        foreach (var item in items)
        {
            var max = Math.Max(Math.Max(item.From, item.To) * 1.1, 0.0001);
            float X(double v) => 8 + labelWidth + (float)(v / max) * track;
            var cy = y + 14;
            DrawText(g, item.Name, Font, ForeColor, 8, cy - Font.Height / 2f);
            using (var rail = new Pen(Color.FromArgb(40, ForeColor), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(rail, X(0), cy, X(max), cy);
            var better = Math.Abs(item.To - item.From) < 0.0001 ? (bool?)null : item.LowerIsBetter ? item.To < item.From : item.To > item.From;
            var color = better is null ? Color.Gray : better.Value ? Good : Bad;
            using (var span = new Pen(color, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(span, X(item.From), cy, X(item.To), cy);
            using (var back = new SolidBrush(BackColor)) g.FillEllipse(back, X(item.From) - 6, cy - 6, 12, 12);
            using (var ring = new Pen(Color.Gray, 2)) g.DrawEllipse(ring, X(item.From) - 6, cy - 6, 12, 12);
            using (var dot = new SolidBrush(ForeColor)) g.FillEllipse(dot, X(item.To) - 6, cy - 6, 12, 12);
            var change = item.From > 0.0001 ? (Math.Abs(item.To - item.From) / item.From < 0.005 ? "  (same)" : $"  ({(item.To - item.From) / item.From * 100:+0;−0}%)") : "";
            using var bold = new Font(Font, FontStyle.Bold);
            DrawText(g, $"{F(item.From, item.Format)} → {F(item.To, item.Format)} {item.Unit}{change}", bold, color, 8 + labelWidth + track + 16, cy - Font.Height / 2f);
            y += 30;
        }
        return y + 4;
    }

    // ----- hover -----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = new PointF(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
        var hit = _hits.LastOrDefault(h => h.Area.Contains(point));
        var text = hit.Text ?? "";
        if (text == _lastTip) return;
        _lastTip = text;
        if (text.Length == 0) _tip.Hide(this); else _tip.Show(text, this, e.X + 14, e.Y + 18, 30000);
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
