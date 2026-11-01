using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CodecSwapper;

/// <summary>Everything the HUD shows and highlights; equal views draw identical pictures, so an unchanged view is not uploaded again.</summary>
public sealed record HudView(
    bool Connected, string Codec, bool CodecVerified, double? Mbps, double? Fps, int TargetMbps, bool H264Selected, bool Busy, string Status,
    string RateControl, int Preset, int VbvFrames, bool Repositioning, string Hand, string History = "",
    string Average = "", string Encoder = "", string Health = "", string Note = "", string OpenMenu = "", bool Interactive = true, string Notice = "", bool NoticeIsError = false, string Latency = "")
{
    public static HudView Empty { get; } = new(false, "—", false, null, null, 0, false, false, "", "vbr", 2, 2, false, "left");

    /// <summary>The recent bitrate readings, oldest first; empty entries are gaps.</summary>
    public IReadOnlyList<double?> Series => [..History.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (double?)d : null)];

    /// <summary>Formats a series for <see cref="History"/> (whole Mbps, so an unchanged picture compares equal).</summary>
    public static string Pack(IEnumerable<double?> values) => string.Join(',', values.Select(v => v is { } d ? Math.Round(d).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-"));
}

public sealed record HudButton(string Id, Rectangle Bounds, string Text, bool Active, bool Enabled);

/// <summary>
/// The HUD's picture and its buttons. Button ids: codec:h264, codec:hevc, slider, menu:rc, menu:preset, menu:vbv, menu:close, rc:vbr, rc:cbr, preset:N, vbv:N,
/// reposition, hudoff, save, cancel, default, size:-, size:+. "grab" is the panel body.
/// </summary>
public static class HudPanel
{
    public const int Width = 640, Height = 624, BitrateStep = 5, SnapMbps = 50, SnapReachMbps = 8;
    /// <summary>The top of SteamVR's own bitrate slider, marked on the HUD's slider; a click close to it lands exactly on it.</summary>
    public const int MarkerMbps = 350, MarkerReachMbps = 15;
    /// <summary>With the dashboard closed there is no input, so only the readouts are drawn, centred in the picture so the panel stays where it was.</summary>
    private const int CompactHeight = 372, CompactOffset = (Height - CompactHeight) / 2;
    public static readonly Rectangle GraphArea = new(20, 200, Width - 40, 120);
    public static readonly Rectangle SliderArea = new(20, 400, Width - 40, 66);
    private const int TrackLeft = 44, TrackRight = Width - 44, TrackY = 446;

    public static readonly (string Id, string Text)[] RateControls =
        [("vbr", "VBR — the bitrate follows the scene (default)"), ("cbr", "CBR — constant bitrate, steadier on a weak link")];
    public static readonly (int Value, string Text)[] Presets =
        [(1, "P1 — fastest, lowest quality"), (2, "P2 — fast (default)"), (3, "P3 — fast, a little better"), (4, "P4 — balanced"),
         (5, "P5 — slower, better quality"), (6, "P6 — slow, high quality"), (7, "P7 — slowest, best quality")];
    public static readonly (int Value, string Text)[] VbvChoices =
        [(1, "1 frame — tightest buffer, least latency"), (2, "2 frames — the default"), (4, "4 frames — smoother bitrate"),
         (8, "8 frames — smoother still, more latency"), (16, "16 frames — loosest buffer")];

    /// <summary>The bitrate a laser at panel x selects: whole steps of <see cref="BitrateStep"/> between the tray's limits, pulled onto every 50 Mbps when close to one (as the tray's slider does).</summary>
    public static int SliderValue(float x)
    {
        var t = Math.Clamp((x - TrackLeft) / (TrackRight - TrackLeft), 0f, 1f);
        var raw = TrayController.MinimumMbps + t * (TrayController.MaximumMbps - TrayController.MinimumMbps);
        var value = Math.Clamp((int)Math.Round(raw / BitrateStep) * BitrateStep, TrayController.MinimumMbps, TrayController.MaximumMbps);
        if (Math.Abs(value - MarkerMbps) <= MarkerReachMbps) return MarkerMbps;
        var nearest = (int)Math.Round(value / (double)SnapMbps) * SnapMbps;
        return Math.Abs(value - nearest) <= SnapReachMbps && nearest >= TrayController.MinimumMbps ? nearest : value;
    }

    public static float SliderX(int mbps) =>
        TrackLeft + (TrackRight - TrackLeft) * (Math.Clamp(mbps, TrayController.MinimumMbps, TrayController.MaximumMbps) - TrayController.MinimumMbps)
        / (float)(TrayController.MaximumMbps - TrayController.MinimumMbps);

    public static string MenuTitle(string menu) => menu switch { "rc" => "Rate control", "preset" => "Encoder preset", "vbv" => "VBV buffer", _ => "" };

    public static IReadOnlyList<HudButton> Layout(HudView view)
    {
        var buttons = new List<HudButton>();
        if (!view.Interactive) return buttons;
        const int margin = 20, gap = 10;
        void Row(int top, int height, (string Id, string Text, bool Active, bool Enabled)[] items)
        {
            var each = (Width - 2 * margin - gap * (items.Length - 1)) / items.Length;
            for (var i = 0; i < items.Length; i++)
                buttons.Add(new(items[i].Id, new(margin + i * (each + gap), top, each, height), items[i].Text, items[i].Active, items[i].Enabled));
        }
        if (view.Repositioning)
        {
            Row(190, 56, [("size:-", "Smaller", false, true), ("size:+", "Larger", false, true)]);
            Row(260, 56, [("save", "Save", false, true), ("cancel", "Cancel", false, true), ("default", "Default", false, true)]);
            return buttons;
        }
        var control = !view.Busy;
        Row(334, 56, [("codec:h264", "H.264", view.H264Selected, control), ("codec:hevc", "Default (HEVC)", !view.H264Selected, control)]);
        if (view.OpenMenu.Length > 0)
        {
            buttons.Add(new("menu:close", new(Width - margin - 140, 396, 140, 34), "Close", false, true));
            var rows = new List<(string Id, string Text, bool Active)>();
            if (view.OpenMenu == "rc") rows.AddRange(RateControls.Select(o => ($"rc:{o.Id}", o.Text, view.RateControl == o.Id)));
            else if (view.OpenMenu == "preset") rows.AddRange(Presets.Select(o => ($"preset:{o.Value}", o.Text, view.Preset == o.Value)));
            else if (view.OpenMenu == "vbv") rows.AddRange(VbvChoices.Select(o => ($"vbv:{o.Value}", o.Text, view.VbvFrames == o.Value)));
            for (var i = 0; i < rows.Count; i++)
                buttons.Add(new(rows[i].Id, new(margin, 438 + i * 25, Width - 2 * margin, 23), rows[i].Text, rows[i].Active, control));
            return buttons;
        }
        buttons.Add(new("slider", SliderArea, "", false, control && view.TargetMbps > 0));
        Row(476, 52, [("menu:rc", $"Rate control: {view.RateControl.ToUpperInvariant()} ▾", false, true), ("menu:preset", $"Preset: P{view.Preset} ▾", false, true),
                      ("menu:vbv", $"VBV: {view.VbvFrames} frame{(view.VbvFrames == 1 ? "" : "s")} ▾", false, true)]);
        Row(540, 52, [("reposition", "Reposition", false, true), ("hudoff", "Turn off HUD", false, true)]);
        return buttons;
    }

    /// <summary>The button under a point in panel pixels (origin top-left), "grab" for the rest of the panel, or null outside it.</summary>
    public static string? HitTest(HudView view, float x, float y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return null;
        foreach (var button in Layout(view))
            if (button.Bounds.Contains((int)x, (int)y)) return button.Enabled ? button.Id : "grab";
        return "grab";
    }

    public static Bitmap Render(HudView view, string? hover)
    {
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var accent = view.Repositioning ? Color.FromArgb(255, 170, 60) : Color.FromArgb(70, 160, 255);
        var compact = !view.Interactive && !view.Repositioning;
        if (compact) g.TranslateTransform(0, CompactOffset);
        using (var path = Rounded(new Rectangle(0, 0, Width - 1, (compact ? CompactHeight : Height) - 1), 26))
        {
            using var back = new SolidBrush(Color.FromArgb(235, 18, 20, 26));
            g.FillPath(back, path);
            using var border = new Pen(accent, 3);
            g.DrawPath(border, path);
        }
        using var title = new Font("Segoe UI Semibold", 15f);
        using var large = new Font("Segoe UI Semibold", 30f);
        using var normal = new Font("Segoe UI", 13f);
        using var small = new Font("Segoe UI", 11f);
        using var white = new SolidBrush(Color.White);
        using var grey = new SolidBrush(Color.FromArgb(170, 178, 190));
        using var accentBrush = new SolidBrush(accent);

        g.DrawString(view.Repositioning ? "Reposition the HUD" : "SteamFrame", title, accentBrush, 22, 14);
        if (view.Repositioning)
        {
            g.DrawString("Hold the trigger on the panel with the other\nhand's laser and move it, then Save.", normal, white, 22, 62);
            g.DrawString($"Attached to the {view.Hand} controller.", small, grey, 22, 128);
        }
        else
        {
            var codecColor = view.Codec == "H.264" ? Color.FromArgb(110, 220, 140) : Color.White;
            using var codecBrush = new SolidBrush(view.Connected ? codecColor : Color.FromArgb(170, 178, 190));
            g.DrawString(view.Codec + (view.CodecVerified ? "" : " ?"), large, codecBrush, 18, 38);
            g.DrawString(view.Mbps is { } m ? $"{m:0} Mbps" : "— Mbps", large, white, 270, 38);
            g.DrawString(view.Fps is { } f ? $"{f:0.0} fps" : "— fps", normal, white, 520, 56);
            g.DrawString(Trim($"{(view.Average.Length > 0 ? view.Average + "    " : "")}target {view.TargetMbps} Mbps", 40), small, grey, 22, 96);
            if (view.Latency.Length > 0)
            {
                using var right = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
                g.DrawString(view.Latency, small, grey, new RectangleF(300, 96, Width - 300 - 22, 22), right);
            }
            g.DrawString(Trim(view.Encoder, 86), small, grey, 22, 120);
            g.DrawString(Trim(view.Health.Length > 0 ? view.Health : "Stream healthy", 86), small, grey, 22, 144);
            if (view.Status.Length > 0) g.DrawString(Trim(view.Status, 86), small, grey, 22, 168);
            if (view.Busy) g.DrawString("Applying…", small, accentBrush, 520, 14);
        }
        if (!view.Repositioning) DrawGraph(g, view, small, accent);
        if (compact) g.DrawString("Open the dashboard to change settings", small, grey, 22, 334);
        if (view.OpenMenu.Length > 0) g.DrawString(MenuTitle(view.OpenMenu), title, accentBrush, 22, 398);
        if (view.Notice.Length > 0 && !view.Repositioning) DrawNotice(g, view, small);
        foreach (var button in Layout(view))
        {
            if (button.Id == "slider") { DrawSlider(g, view, hover == "slider", normal, white, accent); continue; }
            var hot = hover == button.Id && button.Enabled;
            using var shape = Rounded(button.Bounds, 12);
            using var fill = new SolidBrush(button.Active ? Color.FromArgb(accent.R / 2, accent.G / 2, accent.B / 2)
                : Color.FromArgb(hot ? 70 : 40, hot ? 76 : 44, hot ? 90 : 54));
            g.FillPath(fill, shape);
            using var edge = new Pen(button.Active || hot ? accent : Color.FromArgb(70, 76, 90), button.Active ? 2.5f : 1.5f);
            g.DrawPath(edge, shape);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            var font = button.Bounds.Height < 40 ? small : normal;
            g.DrawString(button.Text, font, button.Enabled ? white : grey, button.Bounds, format);
        }
        return bitmap;
    }

    /// <summary>What the tray would have shown as a desktop pop-up, so it is also seen in the headset.</summary>
    private static void DrawNotice(Graphics g, HudView view, Font font)
    {
        var tone = view.NoticeIsError ? Color.FromArgb(255, 110, 100) : Color.FromArgb(255, 190, 80);
        var area = new Rectangle(14, 188, Width - 28, 124);
        using var shape = Rounded(area, 14);
        using var fill = new SolidBrush(Color.FromArgb(245, 38, 28, 12));
        g.FillPath(fill, shape);
        using var edge = new Pen(tone, 2.5f);
        g.DrawPath(edge, shape);
        using var brush = new SolidBrush(Color.White);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisWord };
        g.DrawString(view.Notice, font, brush, new RectangleF(area.Left + 12, area.Top + 8, area.Width - 24, area.Height - 16), format);
    }

    private static void DrawSlider(Graphics g, HudView view, bool hot, Font font, Brush text, Color accent)
    {
        g.DrawString($"Bitrate target   {view.TargetMbps} Mbps", font, text, SliderArea.Left + 4, SliderArea.Top);
        using var rail = new Pen(Color.FromArgb(70, 76, 90), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(rail, TrackLeft, TrackY, TrackRight, TrackY);
        var marker = SliderX(MarkerMbps);
        using var markerPen = new Pen(Color.FromArgb(255, 200, 80), 3);
        g.DrawLine(markerPen, marker, TrackY - 15, marker, TrackY + 15);
        using var markerBrush = new SolidBrush(Color.FromArgb(255, 200, 80));
        using var centred = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString(MarkerMbps.ToString(), font, markerBrush, marker, TrackY - 40, centred);
        var x = SliderX(view.TargetMbps);
        using var filled = new Pen(accent, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(filled, TrackLeft, TrackY, x, TrackY);
        using var knob = new SolidBrush(hot ? Color.White : Color.FromArgb(225, 232, 245));
        g.FillEllipse(knob, x - 15, TrackY - 15, 30, 30);
    }

    private static void DrawGraph(Graphics g, HudView view, Font font, Color accent)
    {
        var area = GraphArea;
        using var back = new SolidBrush(Color.FromArgb(120, 8, 10, 14));
        g.FillRectangle(back, area);
        using var grey = new SolidBrush(Color.FromArgb(150, 158, 170));
        var series = view.Series;
        var peak = Math.Max(Math.Max(view.TargetMbps, series.Max() ?? 0), 50) * 1.15;
        float Y(double mbps) => (float)(area.Bottom - 4 - (area.Height - 8) * Math.Min(mbps / peak, 1));
        if (view.TargetMbps > 0)
        {
            using var dash = new Pen(Color.FromArgb(160, 255, 200, 80), 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawLine(dash, area.Left, Y(view.TargetMbps), area.Right, Y(view.TargetMbps));
            g.DrawString($"{view.TargetMbps}", font, grey, area.Left + 4, Y(view.TargetMbps) - 18);
        }
        if (series.Count < 2) { g.DrawString("Waiting for readings…", font, grey, area.Left + 8, area.Top + 8); return; }
        using var pen = new Pen(Color.FromArgb(110, 220, 140), 2.5f) { LineJoin = LineJoin.Round };
        var step = (area.Width - 8f) / (StatsSamplerHistory - 1);
        var offset = StatsSamplerHistory - series.Count;
        PointF? previous = null;
        for (var i = 0; i < series.Count; i++)
        {
            if (series[i] is not { } value) { previous = null; continue; }
            var point = new PointF(area.Left + 4 + (offset + i) * step, Y(value));
            if (previous is { } last) g.DrawLine(pen, last, point);
            previous = point;
        }
        g.DrawString("60 s", font, grey, area.Left + 4, area.Bottom - 20);
    }

    private const int StatsSamplerHistory = 60;

    /// <summary>The picture as the RGBA bytes the overlay API takes.</summary>
    public static byte[] ToRgba(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[bitmap.Width * bitmap.Height * 4];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (var i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
            return bytes;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static GraphicsPath Rounded(Rectangle r, int radius)
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
}
