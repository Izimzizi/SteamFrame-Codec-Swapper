using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CodecSwapper;

/// <summary>Everything the HUD shows and highlights; equal views draw identical pictures, so an unchanged view is not uploaded again.</summary>
public sealed record HudView(
    bool Connected, string Codec, bool CodecVerified, double? Mbps, double? Fps, int TargetMbps, bool H264Selected, bool Busy, string Status,
    string RateControl, int Preset, int VbvFrames, bool Repositioning, string Hand, string History = "",
    string Average = "", string Encoder = "", string Health = "", string Note = "", string OpenMenu = "", bool Interactive = true, string Notice = "", bool NoticeIsError = false, string Latency = "",
    string Page = "", bool AutoShow = true, bool ExpandOnHover = false, bool Watch = false, string Target = "panel", int FadeAngle = 0, int FadeDistance = 0, int WidthPx = 0, int MultiPass = 0, int Guide = 0, bool OwnLaser = true)
{
    public static HudView Empty { get; } = new(false, "—", false, null, null, 0, false, false, "", "vbr", 2, 2, false, "left");

    /// <summary>The recent bitrate readings, oldest first; empty entries are gaps.</summary>
    public IReadOnlyList<double?> Series => [..History.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (double?)d : null)];

    /// <summary>Formats a series for <see cref="History"/> (whole Mbps, so an unchanged picture compares equal).</summary>
    public static string Pack(IEnumerable<double?> values) => string.Join(',', values.Select(v => v is { } d ? Math.Round(d).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-"));
}

public sealed record HudButton(string Id, Rectangle Bounds, string Text, bool Active, bool Enabled);

/// <summary>
/// The HUD's picture and its buttons. Button ids: codec:h264, codec:hevc, slider, wslider (stream width), menu:rc, menu:preset, menu:vbv, menu:mp, menu:close, rc:vbr, rc:cbr, preset:N, vbv:N, mp:N,
/// page:settings, page:close, set:autoshow:on|off, set:style:panel|watch, set:expand:dashboard|hover, set:ownlaser:on|off, slide:angle, slide:dist (the fade-out sliders), reposition, counters, hide (settings page), save, cancel, default, size:-, size:+. "grab" is the panel body.
/// </summary>
public static class HudPanel
{
    public const int GuideSteps = 5;
    public const int Width = 640, Height = 680, BitrateStep = 5, SnapMbps = 50, SnapReachMbps = 8;
    /// <summary>The top of SteamVR's own bitrate slider, marked on the HUD's slider; a click close to it lands exactly on it.</summary>
    public const int MarkerMbps = 350, MarkerReachMbps = 15;
    /// <summary>With the dashboard closed there is no input, so only the readouts are drawn, at the top of the picture: opening the dashboard grows the panel downward and its top edge stays where it was.</summary>
    private const int CompactHeight = 372;
    public static readonly Rectangle GraphArea = new(20, 200, Width - 40, 120);
    public static readonly Rectangle SliderArea = new(20, 400, Width - 40, 66);
    /// <summary>The stream width slider, under the bitrate one: 0 in the view means the width is not known yet and the slider is inactive.</summary>
    public static readonly Rectangle WidthArea = new(20, 470, Width - 40, 66);
    /// <summary>The top of SteamLink's own width slider, marked on the HUD's slider.</summary>
    public const int WidthMarker = 1344;
    public static readonly int[] WidthChoices = [..Enumerable.Range(0, (StreamWidth.Maximum - StreamWidth.Minimum) / StreamWidth.Step + 1).Select(i => StreamWidth.Minimum + i * StreamWidth.Step)];
    private const int TrackLeft = 44, TrackRight = Width - 44, TrackY = 446;

    /// <summary>The fade-out sliders: the left end is Off (0), then whole steps of five.</summary>
    public static readonly int[] FadeAngleChoices = [0, ..Enumerable.Range(2, 17).Select(i => i * 5)];          // Off, 10-90 degrees
    public static readonly int[] FadeDistanceChoices = [0, ..Enumerable.Range(7, 14).Select(i => i * 5)];       // Off, 35-100 cm
    public static readonly Rectangle FadeAngleArea = new(20, 276, Width - 40, 70);
    public static readonly Rectangle FadeDistanceArea = new(20, 350, Width - 40, 70);

    /// <summary>The value a laser at panel x selects on a settings slider.</summary>
    public static int ChoiceAt(int[] choices, float x) =>
        choices[(int)Math.Round(Math.Clamp((x - TrackLeft) / (TrackRight - TrackLeft), 0f, 1f) * (choices.Length - 1))];

    public static float ChoiceX(int[] choices, int value) =>
        TrackLeft + (TrackRight - TrackLeft) * Math.Max(0, Array.IndexOf(choices, value)) / (float)(choices.Length - 1);

    public static readonly (string Id, string Text)[] RateControls =
        [("vbr", "VBR — the bitrate follows the scene (default)"), ("cbr", "CBR — constant bitrate, steadier on a weak link")];
    public static readonly (int Value, string Text)[] Presets =
        [(1, "P1 — fastest, lowest quality"), (2, "P2 — fast (default)"), (3, "P3 — fast, a little better"), (4, "P4 — balanced"),
         (5, "P5 — slower, better quality"), (6, "P6 — slow, high quality"), (7, "P7 — slowest, best quality")];
    public static readonly (int Value, string Text)[] VbvChoices =
        [(1, "1 frame — tightest buffer, least latency"), (2, "2 frames — the default"), (4, "4 frames — smoother bitrate"),
         (8, "8 frames — smoother still, more latency"), (16, "16 frames — loosest buffer")];

    public static readonly (int Value, string Short, string Text)[] MultiPasses =
        [(0, "Off", "Off — one pass, the least encode time (default)"), (1, "1/4 res", "Quarter resolution first pass — closer to the target, more encode time"),
         (2, "Full res", "Full resolution first pass — closest to the target, the most encode time")];

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

    public static string MenuTitle(string menu) => menu switch { "rc" => "Rate control", "preset" => "Encoder preset", "vbv" => "VBV buffer", "mp" => "Multipass", _ => "" };

    /// <summary>The guide's callout (and its buttons) for a step: what is highlighted, and where the text goes.</summary>
    public static (Rectangle Highlight, Rectangle Callout, string Text) GuideStep(int step)
    {
        var top = new Rectangle(14, 60, Width - 28, 210);
        var bottom = new Rectangle(14, 470, Width - 28, 200);
        return step switch
        {
            1 => (new(10, 30, Width - 20, 296), bottom, "The live stream: the codec, the bitrate it reaches now, the frame rate and a 60 second graph. The dashed line is your bitrate target."),
            2 => (new(14, 326, Width - 28, 72), top, "Choose H.264 (up to 500 Mbps, 1024 px wide) or the stock Default (HEVC) stream. The switch takes a few seconds to apply."),
            3 => (new(14, 392, Width - 28, 150), top, "Bitrate target and stream width. The gold marks, 350 Mbps and 1344 px, are the top of SteamVR's own sliders. Above 350 Mbps needs the bitrate patch."),
            4 => (new(14, 540, Width - 28, 134), top, "Rate control, preset, VBV buffer and multipass tune the encoder. They apply live, and the defaults suit most setups."),
            _ => (new(Width - 20 - 60, 4, 64, 56), bottom, "The cogwheel opens the HUD settings: when the HUD appears, fading, repositioning and hiding. You can replay this guide from there."),
        };
    }

    public static IReadOnlyList<HudButton> GuideLayout(HudView view)
    {
        var callout = GuideStep(view.Guide).Callout;
        var last = view.Guide >= GuideSteps;
        return [new("guide:next", new(callout.Right - 12 - 150, callout.Bottom - 12 - 44, 150, 44), last ? "Finish" : "Next", false, true),
                new("guide:skip", new(callout.Right - 12 - 150 - 10 - 150, callout.Bottom - 12 - 44, 150, 44), "Skip guide", false, true)];
    }

    public static IReadOnlyList<HudButton> Layout(HudView view) => view.Guide > 0 && view.Interactive && !view.Repositioning ? GuideLayout(view) : ControlLayout(view);

    private static IReadOnlyList<HudButton> ControlLayout(HudView view)
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
        if (view.Page == "settings")
        {
            buttons.Add(new("page:close", new(Width - margin - 120, 10, 120, 40), "Back", false, true));
            Row(84, 40, [("set:autoshow:on", "On", view.AutoShow, true), ("set:autoshow:off", "Off", !view.AutoShow, true)]);
            Row(158, 40, [("set:style:watch", "Watch & HUD", view.Watch, true), ("set:style:panel", "HUD only", !view.Watch, true)]);
            if (!view.Watch) Row(232, 40, [("set:expand:dashboard", "Dashboard opens", !view.ExpandOnHover, true), ("set:expand:hover", "Laser points at it", view.ExpandOnHover, true)]);
            buttons.Add(new("slide:angle", FadeAngleArea, "", false, true));
            buttons.Add(new("slide:dist", FadeDistanceArea, "", false, true));
            Row(430, 44, [("reposition", "Reposition HUD", false, true), ("hide", "Hide HUD", false, true)]);
            Row(612, 40, [("set:ownlaser:on", "On", view.OwnLaser, true), ("set:ownlaser:off", "Off", !view.OwnLaser, true)]);
            Row(486, 40, [("guide:replay", "Show the guide again", false, true)]);
            return buttons;
        }
        var control = !view.Busy;
        buttons.Add(new("page:settings", new(Width - margin - 44, 10, 44, 40), "⚙", false, true));
        Row(334, 56, [("codec:h264", "H.264", view.H264Selected, control), ("codec:hevc", "Default (HEVC)", !view.H264Selected, control)]);
        if (view.OpenMenu.Length > 0)
        {
            buttons.Add(new("menu:close", new(Width - margin - 140, 396, 140, 34), "Close", false, true));
            var rows = new List<(string Id, string Text, bool Active)>();
            if (view.OpenMenu == "rc") rows.AddRange(RateControls.Select(o => ($"rc:{o.Id}", o.Text, view.RateControl == o.Id)));
            else if (view.OpenMenu == "preset") rows.AddRange(Presets.Select(o => ($"preset:{o.Value}", o.Text, view.Preset == o.Value)));
            else if (view.OpenMenu == "vbv") rows.AddRange(VbvChoices.Select(o => ($"vbv:{o.Value}", o.Text, view.VbvFrames == o.Value)));
            else if (view.OpenMenu == "mp") rows.AddRange(MultiPasses.Select(o => ($"mp:{o.Value}", o.Text, view.MultiPass == o.Value)));
            for (var i = 0; i < rows.Count; i++)
                buttons.Add(new(rows[i].Id, new(margin, 438 + i * 25, Width - 2 * margin, 23), rows[i].Text, rows[i].Active, control));
            return buttons;
        }
        buttons.Add(new("slider", SliderArea, "", false, control && view.TargetMbps > 0));
        buttons.Add(new("wslider", WidthArea, "", false, control && view.WidthPx > 0));
        Row(546, 52, [("menu:rc", $"Rate control: {view.RateControl.ToUpperInvariant()} ▾", false, true), ("menu:preset", $"Preset: P{view.Preset} ▾", false, true),
                      ("menu:vbv", $"VBV: {view.VbvFrames} frame{(view.VbvFrames == 1 ? "" : "s")} ▾", false, true)]);
        Row(610, 52, [("menu:mp", $"Multipass: {MultiPasses.FirstOrDefault(o => o.Value == view.MultiPass, MultiPasses[0]).Short} ▾", false, true), ("counters", "Reset frame counters", false, true)]);
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
        var settingsPage = view.Page == "settings" && view.Interactive && !view.Repositioning;
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
        using var symbol = new Font("Segoe UI Symbol", 16f);
        using var white = new SolidBrush(Color.White);
        using var grey = new SolidBrush(Color.FromArgb(170, 178, 190));
        using var accentBrush = new SolidBrush(accent);

        g.DrawString(view.Repositioning ? "Reposition the HUD" : settingsPage ? "HUD settings" : "SteamFrame", title, accentBrush, 22, 14);
        if (view.Repositioning)
        {
            g.DrawString(view.Watch ? "Hold the trigger on the panel or the watch icon with\nthe other hand's laser and move it, then Save." : "Hold the trigger on the panel with the other\nhand's laser and move it, then Save.", normal, white, 22, 62);
            g.DrawString($"Attached to the {view.Hand} controller." + (view.Watch ? $"  Smaller/Larger resize the {(view.Target == "icon" ? "watch icon" : "panel")}." : ""), small, grey, 22, 128);
        }
        else if (settingsPage)
        {
            g.DrawString("Show the HUD when SteamVR starts", normal, white, 22, 56);
            g.DrawString("HUD style (Watch & HUD: a round watch on the controller opens it)", normal, white, 22, 130);
            if (view.Watch) g.DrawString("The opened panel is always fully active. The controls only follow\nthe laser or the dashboard in the HUD only style.", small, grey, 22, 204);
            else g.DrawString("Show the controls when", normal, white, 22, 206);
            g.DrawString("HUD laser when the dashboard is closed (turn Off if it steals a game's laser)", small, white, 22, 586);
            g.DrawString("Hide HUD takes the HUD away until SteamVR starts again. Turn it back on in the\ntray menu: VR HUD > Show HUD on a controller.", small, grey, 22, 540);
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
            if (view.Busy) g.DrawString("Applying…", small, accentBrush, 430, 14);
        }
        if (!view.Repositioning && !settingsPage) DrawGraph(g, view, small, accent);
        if (compact) { using var cogFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }; g.DrawString("⚙", symbol, grey, new Rectangle(Width - 20 - 44, 10, 44, 40), cogFormat); }   // the cogwheel stays visible when collapsed, as an invitation to point at the HUD
        if (compact) g.DrawString(view.ExpandOnHover ? "Point the laser at the HUD to change settings" : "Open the dashboard to change settings", small, grey, 22, 334);
        if (view.OpenMenu.Length > 0) g.DrawString(MenuTitle(view.OpenMenu), title, accentBrush, 22, 398);
        if (view.Notice.Length > 0 && !view.Repositioning && !settingsPage) DrawNotice(g, view, small);
        foreach (var button in ControlLayout(view))
        {
            if (button.Id == "slider") { DrawSlider(g, view, hover == "slider", normal, white, accent); continue; }
            if (button.Id == "wslider") { DrawChoiceSlider(g, button.Bounds, "Stream width", view.WidthPx > 0 ? $"{view.WidthPx} px" : "—", WidthChoices, StreamWidth.Snapped(view.WidthPx), hover == button.Id, normal, white, accent, WidthMarker); continue; }
            if (button.Id == "slide:angle") { DrawChoiceSlider(g, button.Bounds, "Fade out when facing more than this away from me", view.FadeAngle == 0 ? "Off" : $"{view.FadeAngle}°", FadeAngleChoices, view.FadeAngle, hover == button.Id, normal, white, accent); continue; }
            if (button.Id == "slide:dist") { DrawChoiceSlider(g, button.Bounds, "Fade out beyond this distance from the headset", view.FadeDistance == 0 ? "Off" : $"{view.FadeDistance / 100.0:0.00} m", FadeDistanceChoices, view.FadeDistance, hover == button.Id, normal, white, accent); continue; }
            var hot = hover == button.Id && button.Enabled;
            using var shape = Rounded(button.Bounds, 12);
            using var fill = new SolidBrush(button.Active ? Color.FromArgb(accent.R / 2, accent.G / 2, accent.B / 2)
                : Color.FromArgb(hot ? 70 : 40, hot ? 76 : 44, hot ? 90 : 54));
            g.FillPath(fill, shape);
            using var edge = new Pen(button.Active || hot ? accent : Color.FromArgb(70, 76, 90), button.Active ? 2.5f : 1.5f);
            g.DrawPath(edge, shape);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            var font = button.Id == "page:settings" ? symbol : button.Bounds.Height < 40 ? small : normal;
            g.DrawString(button.Text, font, button.Enabled ? white : grey, button.Bounds, format);
        }
        if (view.Guide > 0 && view.Interactive && !view.Repositioning) DrawGuide(g, view, hover, title, normal, white);
        return bitmap;
    }

    /// <summary>The guide: everything outside the highlighted part is dimmed, the highlight is outlined in gold and a callout explains it.</summary>
    private static void DrawGuide(Graphics g, HudView view, string? hover, Font title, Font normal, Brush white)
    {
        var (highlight, callout, text) = GuideStep(view.Guide);
        using var dim = new SolidBrush(Color.FromArgb(185, 6, 8, 12));
        using (var all = new Region(new Rectangle(0, 0, Width, Height)))
        {
            all.Exclude(highlight);
            g.FillRegion(dim, all);
        }
        using (var outline = new Pen(Color.FromArgb(255, 200, 80), 3)) using (var frame = Rounded(highlight, 14)) g.DrawPath(outline, frame);
        using (var box = Rounded(callout, 16)) using (var fill = new SolidBrush(Color.FromArgb(250, 28, 32, 42))) using (var edge = new Pen(Color.FromArgb(255, 200, 80), 2.5f)) { g.FillPath(fill, box); g.DrawPath(edge, box); }
        using var gold = new SolidBrush(Color.FromArgb(255, 200, 80));
        g.DrawString($"Guide  {view.Guide} / {GuideSteps}", title, gold, callout.Left + 16, callout.Top + 10);
        g.DrawString(text, normal, white, new RectangleF(callout.Left + 16, callout.Top + 46, callout.Width - 32, callout.Height - 110));
        foreach (var button in GuideLayout(view))
        {
            var hot = hover == button.Id;
            using var shape = Rounded(button.Bounds, 12);
            using var fill = new SolidBrush(button.Id == "guide:next" ? Color.FromArgb(hot ? 150 : 110, 80, 60) : Color.FromArgb(hot ? 70 : 44, hot ? 76 : 48, hot ? 90 : 58));
            g.FillPath(fill, shape);
            using var edge = new Pen(hot || button.Id == "guide:next" ? Color.FromArgb(255, 200, 80) : Color.FromArgb(90, 96, 110), 2);
            g.DrawPath(edge, shape);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(button.Text, normal, white, button.Bounds, format);
        }
    }

    public const int GuideBubbleWidth = 480, GuideBubbleHeight = 200;

    /// <summary>The first thing the guide shows: a bubble that sits above the watch and points down at it.</summary>
    public static Bitmap RenderWatchGuide()
    {
        var bitmap = new Bitmap(GuideBubbleWidth, GuideBubbleHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var gold = Color.FromArgb(255, 200, 80);
        var back = Color.FromArgb(245, 28, 32, 42);
        var box = new Rectangle(4, 4, GuideBubbleWidth - 9, GuideBubbleHeight - 60);
        using var body = Rounded(box, 22);
        using var fill = new SolidBrush(back);
        using var edge = new Pen(gold, 4);
        g.FillPath(fill, body);
        g.DrawPath(edge, body);
        var middle = GuideBubbleWidth / 2f;
        PointF[] arrow = [new(middle - 34, box.Bottom - 2), new(middle + 34, box.Bottom - 2), new(middle, GuideBubbleHeight - 6)];
        g.FillPolygon(fill, arrow);
        g.DrawLines(edge, new[] { arrow[0], arrow[2], arrow[1] });
        using var cover = new Pen(back, 6);
        g.DrawLine(cover, arrow[0].X + 3, box.Bottom - 1, arrow[1].X - 3, box.Bottom - 1);
        using var head = new Font("Segoe UI Semibold", 24f, GraphicsUnit.Pixel);
        using var text = new Font("Segoe UI", 21f, GraphicsUnit.Pixel);
        using var goldBrush = new SolidBrush(gold);
        g.DrawString("Welcome to CodecSwapper", head, goldBrush, 24, 18);
        g.DrawString("Point your laser at this watch and click it\nto open the panel.", text, Brushes.White, new RectangleF(24, 60, GuideBubbleWidth - 48, 80));
        return bitmap;
    }

    public const int IconSize = 128;

    /// <summary>The round watch icon: a 270 degree gauge that fills with the live bitrate (a tick marks the target), the Mbps figure in the middle and the codec under it. Lit when the laser is on it, ringed in orange when the panel is open.</summary>
    public static Bitmap RenderIcon(HudView view, bool hover, bool open)
    {
        var bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var accent = open ? Color.FromArgb(255, 170, 60) : Color.FromArgb(70, 160, 255);
        var disc = new Rectangle(6, 6, IconSize - 12, IconSize - 12);
        using (var back = new SolidBrush(Color.FromArgb(hover ? 245 : 225, hover ? 36 : 18, hover ? 42 : 20, hover ? 54 : 26))) g.FillEllipse(back, disc);
        if (hover || open) { using var ring = new Pen(accent, 4); g.DrawEllipse(ring, disc); }

        const float StartAngle = 135, Sweep = 270;
        var arc = new Rectangle(22, 22, IconSize - 44, IconSize - 44);
        using (var track = new Pen(Color.FromArgb(70, accent), 11) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(track, arc, StartAngle, Sweep);
        var fraction = view.Connected && view.Mbps is { } live ? Math.Clamp(live / TrayController.MaximumMbps, 0, 1) : 0;
        if (fraction > 0.01)
        {
            using var fill = new Pen(accent, 11) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(fill, arc, StartAngle, (float)(Sweep * fraction));
        }
        if (view.TargetMbps > 0)
        {
            var angle = (StartAngle + Sweep * Math.Clamp(view.TargetMbps / (double)TrayController.MaximumMbps, 0, 1)) * Math.PI / 180;
            float cx = IconSize / 2f, cy = IconSize / 2f, r = arc.Width / 2f;
            using var tick = new Pen(Color.White, 3);
            g.DrawLine(tick, cx + (r - 11) * (float)Math.Cos(angle), cy + (r - 11) * (float)Math.Sin(angle), cx + (r + 9) * (float)Math.Cos(angle), cy + (r + 9) * (float)Math.Sin(angle));
        }

        using var middle = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var number = new Font("Segoe UI Semibold", 27f, GraphicsUnit.Pixel);
        using var label = new Font("Segoe UI Semibold", 15f, GraphicsUnit.Pixel);
        g.DrawString(view.Connected && view.Mbps is { } shown ? $"{shown:0}" : "—", number, Brushes.White, new Rectangle(0, 36, IconSize, 34), middle);
        using var codecBrush = new SolidBrush(view.Connected && view.Codec == "H.264" ? Color.FromArgb(110, 220, 140) : Color.FromArgb(190, 196, 206));
        g.DrawString(view.Codec == "H.264" ? "H.264" : view.Codec == "HEVC" ? "HEVC" : "SF", label, codecBrush, new Rectangle(0, 70, IconSize, 20), middle);
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

    private static void DrawChoiceSlider(Graphics g, Rectangle area, string label, string shown, int[] choices, int value, bool hot, Font font, Brush text, Color accent, int? marked = null)
    {
        g.DrawString(label, font, text, area.Left + 2, area.Top);
        using var right = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(shown, font, text, new RectangleF(area.Left, area.Top, area.Width - 2, 26), right);
        var y = area.Top + 46;
        using var rail = new Pen(Color.FromArgb(70, 76, 90), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(rail, TrackLeft, y, TrackRight, y);
        if (marked is { } mark && choices.Contains(mark))
        {
            // The top of SteamVR's own slider, marked like the 350 on the bitrate slider.
            var at = ChoiceX(choices, mark);
            using var markerPen = new Pen(Color.FromArgb(255, 200, 80), 3);
            g.DrawLine(markerPen, at, y - 15, at, y + 15);
            using var markerBrush = new SolidBrush(Color.FromArgb(255, 200, 80));
            using var centred = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(mark.ToString(), font, markerBrush, at, y - 40, centred);
        }
        var x = ChoiceX(choices, value);
        using var filled = new Pen(accent, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(filled, TrackLeft, y, x, y);
        using var knob = new SolidBrush(hot ? Color.White : Color.FromArgb(225, 232, 245));
        g.FillEllipse(knob, x - 15, y - 15, 30, 30);
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
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2 - 1);
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
