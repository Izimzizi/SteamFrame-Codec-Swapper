using System.Drawing.Drawing2D;

namespace SteamFrameTray;

/// <summary>Live encoded bitrate with its 60 s average, FPS, dropped frames and the verified active codec. The window sizes itself to its content.</summary>
public sealed class StatsForm : Form
{
    private readonly StatsService _service;
    private readonly Label _codec = Big("—"), _mbps = Big("—"), _average = Small(), _fps = Small(), _target = Small(), _encoder = Small(), _dropped = Small(), _errors = Small(), _note = Small();
    private readonly GraphPanel _graph = new() { Height = 150, Dock = DockStyle.Top, MinimumSize = new Size(480, 150) };

    public StatsForm(StatsService service)
    {
        _service = service;
        Text = "Stream stats";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(500, 0);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        TopMost = true;
        ShowIcon = false;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(12, 8, 12, 8) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var top = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(Caption("Codec"), 0, 0); top.Controls.Add(Caption("Bitrate (encoded)"), 1, 0);
        var rate = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        _average.Margin = new Padding(12, 18, 0, 0);
        rate.Controls.Add(_mbps); rate.Controls.Add(_average);
        top.Controls.Add(_codec, 0, 1); top.Controls.Add(rate, 1, 1);

        // Each reading is a line of its own, so a long one never pushes another out of view.
        var detail = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 6, 0, 6) };
        detail.Controls.AddRange([_fps, _target, _encoder, _dropped, _errors]);
        _note.MaximumSize = new Size(480, 0);

        root.Controls.Add(top); root.Controls.Add(detail); root.Controls.Add(_graph); root.Controls.Add(_note);
        Controls.Add(root);

        service.Sampled += OnSampled;
        VisibleChanged += (_, _) => service.Viewing = Visible;
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    private void OnSampled(StatsSample sample, TraySnapshot tray)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnSampled(sample, tray)); return; }
        _codec.Text = sample.SteamVrRunning ? sample.Codec : "SteamVR off";
        _codec.ForeColor = sample.CodecVerified ? Color.FromArgb(46, 160, 67) : SystemColors.GrayText;
        _mbps.Text = sample.Mbps is double m ? $"{m:0} Mbps" : "—";
        _average.Text = _service.Sampler.RecentAverage is double average ? $"avg 60 s: {average:0} Mbps" : "";
        _fps.Text = sample.Fps is double f ? $"{f:0.0} fps" : "— fps";
        _target.Text = $"target {tray.TargetMbps} Mbps";
        _encoder.Text = sample.Config is { } c
            ? $"encoder asked for {c.AverageMbps:0} Mbps avg / {c.MaxMbps:0} peak ({c.RateControl}{(c.Fps is double cf ? $", {cf:0} fps" : "")})"
            : "";
        _dropped.Text = sample.Health is { } h
            ? $"dropped frames (sender held back): {h.HeldTotal} total / {h.HeldLastMinute} in last 60 s" + (h.CompositorAvailable ? $"    PC: {h.CompositorDropped} dropped, {h.CompositorLate} late" : "")
            : "";
        _errors.Text = sample.EncodeErrors > 0 ? $"encode errors {sample.EncodeErrors}" : "";
        _note.Text = (_service.Recording ? $"Recording to {_service.LogPath}\n" : "") + (sample.Note ?? sample.CodecDetail);
        foreach (var label in new[] { _encoder, _dropped, _errors, _note }) label.Visible = label.Text.Length > 0;
        _graph.Update(_service.Sampler.History, tray.TargetMbps);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _service.Sampled -= OnSampled;
        base.Dispose(disposing);
    }

    private static Label Big(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 20, FontStyle.Bold) };
    private static Label Small() => new() { AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText };

    private sealed class GraphPanel : Panel
    {
        private IReadOnlyList<double?> _values = [];
        private double? _target;
        public GraphPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        public void Update(IReadOnlyList<double?> values, double? target) { _values = values.ToArray(); _target = target; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var area = new Rectangle(10, 6, Math.Max(10, Width - 20), Math.Max(10, Height - 18));
            using var border = new Pen(Color.FromArgb(90, SystemColors.GrayText));
            g.DrawRectangle(border, area);
            var top = Math.Max(50, Math.Ceiling(Math.Max(_values.Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(0).Max(), _target ?? 0) / 50) * 50);
            using var grid = new Pen(Color.FromArgb(40, SystemColors.GrayText));
            using var small = new Font("Segoe UI", 8);
            for (var line = 1; line < 4; line++)
            {
                var y = area.Bottom - (int)(area.Height * line / 4.0);
                g.DrawLine(grid, area.Left, y, area.Right, y);
            }
            g.DrawString($"{top:0} Mbps", small, SystemBrushes.GrayText, area.Left + 3, area.Top + 1);
            if (_target is double target)
            {
                var y = area.Bottom - (int)(area.Height * Math.Min(1, target / top));
                using var dash = new Pen(Color.FromArgb(160, 210, 153, 34)) { DashStyle = DashStyle.Dash };
                g.DrawLine(dash, area.Left, y, area.Right, y);
            }
            using var line2 = new Pen(Color.FromArgb(47, 129, 247), 2);
            var step = area.Width / (double)(StatsSampler.HistoryLength - 1);
            var offset = StatsSampler.HistoryLength - _values.Count;
            PointF? previous = null;
            for (var i = 0; i < _values.Count; i++)
            {
                if (_values[i] is not double value) { previous = null; continue; }
                var point = new PointF((float)(area.Left + (offset + i) * step), (float)(area.Bottom - area.Height * Math.Min(1, value / top)));
                if (previous is PointF p) g.DrawLine(line2, p, point);
                previous = point;
            }
        }
    }
}
