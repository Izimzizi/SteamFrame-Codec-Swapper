using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace SteamFrameTray;

/// <summary>Everything the HUD shows and highlights; equal views draw identical pictures, so an unchanged view is not uploaded again.</summary>
public sealed record HudView(
    bool Connected, string Codec, bool CodecVerified, double? Mbps, double? Fps, int TargetMbps, bool H264Selected, bool Busy, string Status,
    string RateControl, int Preset, int VbvFrames, long Late, long Dropped, bool Repositioning, string Hand, string History = "")
{
    public static HudView Empty { get; } = new(false, "—", false, null, null, 0, false, false, "", "vbr", 2, 2, 0, 0, false, "left");

    /// <summary>The recent bitrate readings, oldest first; empty entries are gaps.</summary>
    public IReadOnlyList<double?> Series => [..History.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (double?)d : null)];

    /// <summary>Formats a series for <see cref="History"/> (whole Mbps, so an unchanged picture compares equal).</summary>
    public static string Pack(IEnumerable<double?> values) => string.Join(',', values.Select(v => v is { } d ? Math.Round(d).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-"));
}

public sealed record HudButton(string Id, Rectangle Bounds, string Text, bool Active, bool Enabled);

/// <summary>The HUD's picture and its buttons. Button ids: codec:h264, codec:hevc, rate:-, rate:+, rc:vbr, rc:cbr, preset:N, vbv:N, reposition, save, cancel, default, size:-, size:+. "grab" is the panel body.</summary>
public static class HudPanel
{
    public const int Width = 512, Height = 520, BitrateStep = 10;
    private const int Shift = 120; // the graph sits between the readouts and the controls
    public static readonly Rectangle GraphArea = new(20, 138, Width - 40, 108);
    public static readonly int[] Presets = [1, 2, 4, 6, 7];
    public static readonly int[] VbvChoices = [1, 2, 4, 8, 16];

    public static IReadOnlyList<HudButton> Layout(HudView view)
    {
        var buttons = new List<HudButton>();
        const int margin = 20, gap = 10;
        void Row(int top, int height, (string Id, string Text, bool Active, bool Enabled)[] items, int leftEdge = margin, int rightEdge = Width - margin)
        {
            var each = (rightEdge - leftEdge - gap * (items.Length - 1)) / items.Length;
            for (var i = 0; i < items.Length; i++)
                buttons.Add(new(items[i].Id, new(leftEdge + i * (each + gap), top, each, height), items[i].Text, items[i].Active, items[i].Enabled));
        }
        if (view.Repositioning)
        {
            Row(262 + Shift - 80, 56, [("size:-", "Smaller", false, true), ("size:+", "Larger", false, true)]);
            Row(330 + Shift - 80, 56, [("save", "Save", false, true), ("cancel", "Cancel", false, true), ("default", "Default", false, true)]);
            return buttons;
        }
        var control = !view.Busy;
        Row(150 + Shift, 52, [("codec:h264", "H.264", view.H264Selected, control), ("codec:hevc", "Default (HEVC)", !view.H264Selected, control)]);
        // Bitrate: minus, the value, plus.
        buttons.Add(new("rate:-", new(margin, 212 + Shift, 80, 46), "−", false, control && view.TargetMbps > 0));
        buttons.Add(new("rate:+", new(Width - margin - 80, 212 + Shift, 80, 46), "+", false, control && view.TargetMbps > 0));
        Row(268 + Shift, 44, [("rc:vbr", "VBR", view.RateControl == "vbr", control), ("rc:cbr", "CBR", view.RateControl == "cbr", control)]);
        Row(318 + Shift, 36, [..Presets.Select(p => ($"preset:{p}", $"P{p}", view.Preset == p, control))]);
        Row(360 + Shift, 30, [..VbvChoices.Select(v => ($"vbv:{v}", $"V{v}", view.VbvFrames == v, control))], margin + 76);
        buttons.Add(new("reposition", new(margin, 360 + Shift, 64, 30), "Move", false, true));
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
        using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 26))
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

        g.DrawString(view.Repositioning ? "Move the HUD" : "SteamFrame", title, accentBrush, 22, 14);
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
            var mbps = view.Mbps is { } m ? $"{m:0} Mbps" : "— Mbps";
            var fps = view.Fps is { } f ? $"{f:0} fps" : "— fps";
            g.DrawString($"{mbps}   {fps}", normal, white, 250, 52);
            var late = view.Late + view.Dropped;
            g.DrawString(view.Status.Length > 0 ? Trim(view.Status, 44) : (late > 0 ? $"{view.Late} late, {view.Dropped} dropped" : "Stream healthy"), small, grey, 22, 106);
            if (view.Busy) g.DrawString("Applying…", small, accentBrush, 400, 14);
        }
        if (!view.Repositioning) DrawGraph(g, view, small, accent);
        foreach (var button in Layout(view))
        {
            var hot = hover == button.Id && button.Enabled;
            using var shape = Rounded(button.Bounds, 12);
            using var fill = new SolidBrush(button.Active ? Color.FromArgb(accent.R / 2, accent.G / 2, accent.B / 2)
                : Color.FromArgb(hot ? 70 : 40, hot ? 76 : 44, hot ? 90 : 54));
            g.FillPath(fill, shape);
            using var edge = new Pen(button.Active || hot ? accent : Color.FromArgb(70, 76, 90), button.Active ? 2.5f : 1.5f);
            g.DrawPath(edge, shape);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var font = button.Bounds.Height < 40 ? small : normal;
            g.DrawString(button.Text, font, button.Enabled ? white : grey, button.Bounds, format);
        }
        if (!view.Repositioning)
        {
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString($"{view.TargetMbps} Mbps target", normal, white, new Rectangle(100, 212 + Shift, Width - 200, 46), format);
        }
        return bitmap;
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
