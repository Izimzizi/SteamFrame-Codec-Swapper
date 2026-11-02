using System.Numerics;
using CodecSwapper;

namespace CodecSwapperTests;

public sealed class HudMathTests
{
    private static void Near(Vector3 a, Vector3 b) => Assert.True(Vector3.Distance(a, b) < 1e-4f, $"{a} vs {b}");

    [Fact]
    public void A_pose_followed_by_its_inverse_is_the_identity()
    {
        var pose = Pose.Rotation(30, -50, 12, new(.1f, .2f, -.3f));
        var round = pose.Then(pose.Inverse());
        Near(round.X, Vector3.UnitX); Near(round.Y, Vector3.UnitY); Near(round.Z, Vector3.UnitZ); Near(round.T, Vector3.Zero);
    }

    [Fact]
    public void The_default_placement_lies_facing_up_and_sits_behind_the_controller_toward_the_user()
    {
        var normal = HudSettings.DefaultPose.Rotate(Vector3.UnitZ);
        Assert.True(normal.Y > .9f, normal.ToString());
        Assert.True(HudSettings.DefaultPose.T.Z > .25f, HudSettings.DefaultPose.T.ToString());
        Assert.Equal(.2135f, HudSettings.DefaultWidth);
    }

    [Fact]
    public void The_matrix_round_trip_keeps_the_pose()
    {
        var pose = Pose.Rotation(10, 20, 30, new(1, 2, 3));
        Assert.True(Pose.TryFromMatrix34(pose.ToMatrix34(), out var back));
        Near(back.X, pose.X); Near(back.T, pose.T);
    }

    [Fact]
    public void The_matrix_is_row_major_with_the_translation_in_the_last_column()
    {
        var matrix = new Pose(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new(4, 5, 6)).ToMatrix34();
        Assert.Equal([1f, 0, 0, 4, 0, 1, 0, 5, 0, 0, 1, 6], matrix);
    }

    [Fact]
    public void Unusable_matrices_are_rejected()
    {
        Assert.False(Pose.TryFromMatrix34(null, out _));
        Assert.False(Pose.TryFromMatrix34(new float[12], out _));
        Assert.False(Pose.TryFromMatrix34([..Enumerable.Repeat(float.NaN, 12)], out _));
        Assert.False(Pose.TryFromMatrix34(new float[5], out _));
    }
}

public sealed class HudSettingsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "hud-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Missing_or_damaged_files_give_the_defaults()
    {
        Assert.True(HudSettings.Load(_file).Enabled);
        File.WriteAllText(_file, "{ not json");
        var loaded = HudSettings.Load(_file);
        Assert.True(loaded.IsLeft);
        Assert.Equal(HudSettings.DefaultPose, loaded.Pose);
    }

    [Fact]
    public void A_saved_placement_and_hand_come_back()
    {
        var settings = new HudSettings { Enabled = true, Hand = "right" };
        settings.SetPlacement(Pose.Rotation(5, -20, 0, new(.01f, .1f, .02f)), .3f);
        settings.Save(_file);
        var loaded = HudSettings.Load(_file);
        Assert.True(loaded.Enabled);
        Assert.False(loaded.IsLeft);
        Assert.Equal(.3f, loaded.WidthMeters);
        Assert.True(Vector3.Distance(loaded.Pose.T, new(.01f, .1f, .02f)) < 1e-5f);
    }

    [Fact]
    public void Resetting_returns_to_the_default_place_and_size()
    {
        var settings = new HudSettings();
        settings.SetPlacement(Pose.Identity, .4f);
        settings.ResetPlacement();
        Assert.Equal(HudSettings.DefaultPose, settings.Pose);
        Assert.Equal(HudSettings.DefaultWidth, settings.WidthMeters);
    }

    [Fact]
    public void Width_stays_within_its_limits()
    {
        var settings = new HudSettings();
        settings.SetPlacement(Pose.Identity, 5f);
        Assert.Equal(HudSettings.MaximumWidth, settings.WidthMeters);
        settings.SetPlacement(Pose.Identity, 0f);
        Assert.Equal(HudSettings.MinimumWidth, settings.WidthMeters);
    }

    public void Dispose() { try { File.Delete(_file); } catch (IOException) { } }
}

public sealed class HudPanelTests
{
    private static HudView View(bool reposition = false, bool busy = false) => HudView.Empty with { Connected = true, TargetMbps = 300, Repositioning = reposition, Busy = busy };

    [Fact]
    public void The_laser_finds_each_button_by_its_centre()
    {
        foreach (var button in HudPanel.Layout(View()))
        {
            var centre = button.Bounds;
            Assert.Equal(button.Id, HudPanel.HitTest(View(), centre.X + centre.Width / 2f, centre.Y + centre.Height / 2f));
        }
    }

    [Fact]
    public void Buttons_never_overlap_each_other_or_leave_the_panel()
    {
        foreach (var view in new[] { View(), View(reposition: true), View() with { Page = "settings" }, View() with { Page = "settings", Watch = true } })
        {
            var buttons = HudPanel.Layout(view);
            foreach (var button in buttons) Assert.True(new System.Drawing.Rectangle(0, 0, HudPanel.Width, HudPanel.Height).Contains(button.Bounds), button.Id);
            for (var i = 0; i < buttons.Count; i++)
                for (var j = i + 1; j < buttons.Count; j++)
                    Assert.False(buttons[i].Bounds.IntersectsWith(buttons[j].Bounds), $"{buttons[i].Id} overlaps {buttons[j].Id}");
        }
    }

    [Fact]
    public void The_rest_of_the_panel_is_the_grab_area_and_outside_is_nothing()
    {
        Assert.Equal("grab", HudPanel.HitTest(View(), 5, 5));
        Assert.Null(HudPanel.HitTest(View(), -1, 10));
        Assert.Null(HudPanel.HitTest(View(), 10, HudPanel.Height));
    }

    [Fact]
    public void While_busy_the_controls_are_inert()
    {
        var codec = HudPanel.Layout(View(busy: true)).Single(b => b.Id == "codec:h264");
        Assert.Equal("grab", HudPanel.HitTest(View(busy: true), codec.Bounds.X + 5, codec.Bounds.Y + 5));
    }

    [Fact]
    public void Repositioning_offers_only_the_placement_buttons()
    {
        var ids = HudPanel.Layout(View(reposition: true)).Select(b => b.Id).ToArray();
        Assert.Equal(["size:-", "size:+", "save", "cancel", "default"], ids);
    }

    [Fact]
    public void The_history_round_trips_and_keeps_its_gaps()
    {
        var packed = HudView.Pack([100.4, null, 250.6]);
        Assert.Equal("100,-,251", packed);
        Assert.Equal<double?>([100, null, 251], (HudView.Empty with { History = packed }).Series.ToArray());
        Assert.Empty((HudView.Empty with { History = "" }).Series);
    }

    [Fact]
    public void The_graph_is_drawn_inside_its_area_and_clear_of_the_buttons()
    {
        foreach (var button in HudPanel.Layout(View())) Assert.False(button.Bounds.IntersectsWith(HudPanel.GraphArea), button.Id);
        var view = View() with { History = HudView.Pack(Enumerable.Range(0, 60).Select(i => (double?)(200 + i))) };
        using var bitmap = HudPanel.Render(view, null);
        var line = false;
        for (var x = HudPanel.GraphArea.Left; x < HudPanel.GraphArea.Right && !line; x += 3)
            for (var y = HudPanel.GraphArea.Top; y < HudPanel.GraphArea.Bottom; y++)
                if (bitmap.GetPixel(x, y) is { G: > 200, R: < 140 }) { line = true; break; }
        Assert.True(line, "no green graph line found");
    }

    [Fact]
    public void The_picture_is_the_declared_size_in_rgba()
    {
        using var bitmap = HudPanel.Render(View(), "codec:h264");
        Assert.Equal(HudPanel.Width * HudPanel.Height * 4, HudPanel.ToRgba(bitmap).Length);
    }
}

public sealed class HudControllerTests
{
    private sealed class FakeApi : IOverlayApi
    {
        public bool Connected { get; private set; }
        public bool Running = true, CanConnect = true, Dashboard = true;
        public uint Left = 3, Right = 4;
        public readonly List<string> Calls = [];
        public readonly Queue<OverlayEvent> Events = new(), IconEvents = new();
        public readonly Dictionary<ulong, bool> Visible = [];
        private ulong _created = 6;
        public readonly Dictionary<uint, Pose> Poses = [];
        public Pose LastRelative; public uint LastDevice = IOverlayApi.NoDevice; public float LastWidth; public int Uploads;
        public bool TryConnect(out string error) { error = CanConnect ? "" : "no SteamVR"; if (CanConnect) { Connected = true; Calls.Add("connect"); } return CanConnect; }
        public void Disconnect() { Connected = false; Calls.Add("disconnect"); }
        public bool StillRunning() => Running;
        public ulong CreateOverlay(string key, string name) { Calls.Add("create"); return ++_created; }
        public void DestroyOverlay(ulong overlay) => Calls.Add("destroy");
        public void SetWidth(ulong overlay, float meters) => LastWidth = meters;
        public void SetAlpha(ulong overlay, float alpha) { }
        public void EnableLaser(ulong overlay, int w, int h) => Calls.Add($"laser {w}x{h}");
        public void SetImage(ulong overlay, byte[] rgba, int width, int height) => Uploads++;
        public void SetVisible(ulong overlay, bool visible) { Calls.Add(visible ? "show" : "hide"); Visible[overlay] = visible; }
        public uint ControllerIndex(bool left) => left ? Left : Right;
        public void AttachToDevice(ulong overlay, uint device, Pose relative) { LastDevice = device; LastRelative = relative; }
        public bool TryDevicePose(uint device, out Pose pose) => Poses.TryGetValue(device, out pose);
        public IReadOnlyList<OverlayEvent> PollEvents(ulong overlay) { var queue = overlay == 7 ? Events : IconEvents; var list = queue.ToList(); queue.Clear(); return list; }
        public bool DashboardVisible() => Dashboard;
        public void Dispose() { }
    }

    private sealed class Actions : IHudActions
    {
        public readonly List<string> Calls = [];
        public Task SetCodecAsync(bool h264) { Calls.Add("codec " + h264); return Task.CompletedTask; }
        public Task SetBitrateAsync(int mbps) { Calls.Add("rate " + mbps); return Task.CompletedTask; }
        public Task SetEncoderAsync(string rc, int preset, int vbv) { Calls.Add($"enc {rc} P{preset} V{vbv}"); return Task.CompletedTask; }
        public Task HideAsync() { Calls.Add("hud off"); return Task.CompletedTask; }
    }

    private sealed class Rig
    {
        public readonly FakeApi Api = new();
        public readonly Actions Actions = new();
        public readonly HudSettings Settings = new() { Enabled = true, Watch = false };
        public int Saves; public bool SteamVr = true;
        public HudView View = HudView.Empty with { Connected = true, TargetMbps = 300, RateControl = "vbr", Preset = 2, VbvFrames = 2 };
        public readonly HudController Hud;
        private DateTime _clock = new(2026, 1, 1);
        public Rig() => Hud = new(Api, Settings, () => View, Actions, () => Saves++, () => SteamVr);
        public void Tick() { _clock += TimeSpan.FromSeconds(1); Hud.Tick(_clock); }
        // Laser click at a button's centre (GL space: y counts up from the bottom).
        public OverlayEvent Click(string id, OverlayEventKind kind = OverlayEventKind.MouseDown, uint device = 4)
        {
            var b = HudPanel.Layout(View with { Repositioning = Hud.Repositioning, OpenMenu = Hud.OpenMenu, Page = Hud.Page }).Single(x => x.Id == id).Bounds;
            return new(kind, device, b.X + b.Width / 2f, HudPanel.Height - (b.Y + b.Height / 2f));
        }
        public void Press(string id) { Api.Events.Enqueue(Click(id)); Tick(); }
        // Laser at panel x on the slider's line (GL space).
        public OverlayEvent Slide(OverlayEventKind kind, float x, uint device = 4) => new(kind, device, x, HudPanel.Height - (HudPanel.SliderArea.Top + 40));
    }

    [Fact]
    public void In_watch_style_the_panel_waits_behind_a_round_icon_that_opens_and_closes_it()
    {
        var rig = new Rig();
        rig.Settings.Watch = true;
        rig.Tick();
        Assert.False(rig.Api.Visible[7]);
        Assert.True(rig.Api.Visible[8]);
        Assert.Equal(HudSettings.IconPose, rig.Api.LastRelative);
        rig.Api.IconEvents.Enqueue(new(OverlayEventKind.MouseDown, 4, 60, 60)); rig.Tick();
        Assert.True(rig.Api.Visible[7]);
        Assert.True(rig.Settings.PanelOpen);
        rig.Api.IconEvents.Enqueue(new(OverlayEventKind.MouseDown, 4, 60, 60)); rig.Tick();
        Assert.False(rig.Api.Visible[7]);
    }

    [Fact]
    public void Closing_the_panel_from_the_icon_drops_a_half_finished_interaction()
    {
        var rig = new Rig();
        rig.Settings.Watch = true; rig.Settings.PanelOpen = true;
        rig.Tick();
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseMove, 4, 100, 100));
        rig.Press("menu:rc");
        Assert.Equal("rc", rig.Hud.OpenMenu);
        rig.Api.IconEvents.Enqueue(new(OverlayEventKind.MouseDown, 4, 60, 60)); rig.Tick();
        Assert.Equal("", rig.Hud.OpenMenu);
    }

    [Fact]
    public void Choosing_the_always_visible_style_removes_the_icon_and_keeps_the_panel()
    {
        var rig = new Rig();
        rig.Settings.Watch = true; rig.Settings.PanelOpen = true;
        rig.Tick();
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseMove, 4, 100, 100));
        rig.Press("page:settings");
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseMove, 4, 100, 100));
        rig.Press("set:style:panel");
        rig.Tick();
        Assert.False(rig.Settings.Watch);
        Assert.Contains("destroy", rig.Api.Calls);
        Assert.True(rig.Api.Visible[7]);
        Assert.True(rig.Saves > 0);
    }

    [Fact]
    public void It_waits_for_SteamVR_and_the_setting_before_touching_anything()
    {
        var rig = new Rig { SteamVr = false };
        rig.Tick();
        rig.SteamVr = true; rig.Settings.Enabled = false;
        rig.Tick();
        Assert.Empty(rig.Api.Calls);
    }

    [Fact]
    public void It_joins_creates_the_panel_and_hangs_it_on_the_chosen_controller()
    {
        var rig = new Rig();
        rig.Tick();
        Assert.Equal(["connect", "create", $"laser {HudPanel.Width}x{HudPanel.Height}", "show"], rig.Api.Calls);
        Assert.Equal(3u, rig.Api.LastDevice);
        Assert.Equal(HudSettings.DefaultPose, rig.Api.LastRelative);
        Assert.Equal(1, rig.Api.Uploads);
        rig.Settings.Hand = "right";
        rig.Tick();
        Assert.Equal(4u, rig.Api.LastDevice);
    }

    [Fact]
    public void It_leaves_SteamVR_when_it_stops_and_retries_when_it_is_back()
    {
        var rig = new Rig();
        rig.Tick();
        rig.SteamVr = false; rig.Tick();
        Assert.False(rig.Api.Connected);
        Assert.Contains("destroy", rig.Api.Calls);
        rig.SteamVr = true; rig.Tick();
        Assert.True(rig.Api.Connected);
    }

    [Fact]
    public void A_refused_connection_is_retried_later_not_every_tick()
    {
        var rig = new Rig();
        rig.Api.CanConnect = false;
        rig.Hud.Tick(new DateTime(2026, 1, 1));
        rig.Hud.Tick(new DateTime(2026, 1, 1).AddSeconds(1));
        Assert.Empty(rig.Api.Calls);
        rig.Api.CanConnect = true;
        rig.Hud.Tick(new DateTime(2026, 1, 1).AddSeconds(4));
        Assert.True(rig.Api.Connected);
    }

    [Fact]
    public void The_panel_hides_while_its_controller_is_not_tracked_and_returns_with_it()
    {
        var rig = new Rig();
        rig.Tick();
        rig.Api.Left = IOverlayApi.NoDevice; rig.Tick();
        Assert.Equal("hide", rig.Api.Calls[^1]);
        rig.Api.Left = 5; rig.Tick();
        Assert.Equal(5u, rig.Api.LastDevice);
        Assert.Equal("show", rig.Api.Calls[^1]);
    }

    [Fact]
    public void With_the_dashboard_closed_the_HUD_shows_readouts_only_and_ignores_the_laser()
    {
        var rig = new Rig();
        rig.Tick();
        rig.Api.Dashboard = false;
        rig.Api.Events.Enqueue(rig.Click("codec:h264"));
        rig.Tick();
        Assert.Empty(rig.Actions.Calls);
        Assert.Empty(HudPanel.Layout(rig.View with { Interactive = false }));
        rig.Api.Dashboard = true;
        rig.Press("codec:h264");
        Assert.Equal(["codec True"], rig.Actions.Calls);
    }

    [Fact]
    public void A_dashboard_SteamVR_does_not_report_is_recognised_when_the_laser_moves_over_the_panel()
    {
        var rig = new Rig();
        rig.Tick();
        rig.Api.Dashboard = false;   // already open when the HUD joined, but reported closed
        rig.Api.Events.Enqueue(rig.Click("codec:h264", OverlayEventKind.MouseMove));
        rig.Api.Events.Enqueue(rig.Click("codec:h264"));
        rig.Tick();
        Assert.Equal(["codec True"], rig.Actions.Calls);
    }

    [Fact]
    public void The_Hide_HUD_button_asks_the_tray_to_hide_the_HUD()
    {
        var rig = new Rig();
        rig.Tick();
        rig.Press("hide");
        Assert.Equal(["hud off"], rig.Actions.Calls);
    }

    [Fact]
    public void The_cogwheel_opens_a_settings_page_with_the_options_and_Reposition_and_Back_returns()
    {
        var rig = new Rig();
        rig.Tick();
        Assert.DoesNotContain(HudPanel.Layout(rig.View with { Interactive = true }), b => b.Id == "reposition");
        rig.Press("page:settings");
        Assert.Equal("settings", rig.Hud.Page);
        rig.Press("set:autoshow:off");
        Assert.False(rig.Settings.AutoShow);
        rig.Press("reposition");
        Assert.True(rig.Hud.Repositioning);
        rig.Press("cancel");
        Assert.Equal("settings", rig.Hud.Page);
        rig.Press("page:close");
        Assert.Equal("", rig.Hud.Page);
        rig.Press("page:settings");
        rig.Press("set:expand:hover");
        Assert.True(rig.Settings.ExpandOnHover);
        Assert.Equal(2, rig.Saves);
        using var bitmap = HudPanel.Render(HudView.Empty with { Connected = true, Interactive = true, Page = "settings" }, null);
        Assert.Equal(HudPanel.Width, bitmap.Width);
    }

    [Fact]
    public void Hiding_lasts_one_SteamVR_session_and_Show_when_SteamVR_starts_decides_the_next()
    {
        var file = Path.GetTempFileName();
        try
        {
            new HudSettings { Enabled = false, AutoShow = true }.Save(file);
            Assert.True(HudSettings.Load(file).Enabled);
            new HudSettings { Enabled = true, AutoShow = false }.Save(file);
            Assert.False(HudSettings.Load(file).Enabled);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void With_expand_on_hover_the_controls_follow_the_laser_not_the_dashboard()
    {
        var rig = new Rig();
        rig.Settings.ExpandOnHover = true;
        rig.Tick();
        rig.Api.Dashboard = true;
        rig.Tick();
        rig.Tick();
        rig.Tick();
        rig.Api.Events.Enqueue(rig.Click("codec:h264", OverlayEventKind.MouseMove));
        rig.Api.Events.Enqueue(rig.Click("codec:h264"));
        rig.Tick();
        Assert.Equal(["codec True"], rig.Actions.Calls);
        rig.Tick(); rig.Tick();
        rig.Api.Events.Enqueue(rig.Click("codec:hevc"));
        rig.Tick();
        Assert.Equal(["codec True"], rig.Actions.Calls);
    }

    [Fact]
    public void With_expand_on_hover_the_panel_shrinks_as_soon_as_the_laser_leaves_it()
    {
        var rig = new Rig();
        rig.Settings.ExpandOnHover = true;
        rig.Tick();
        rig.Api.Events.Enqueue(rig.Click("codec:h264", OverlayEventKind.MouseMove));
        rig.Tick();
        var uploads = rig.Api.Uploads;
        rig.Api.Events.Enqueue(new(OverlayEventKind.FocusLeave, 4, 0, 0));
        rig.Api.Events.Enqueue(rig.Click("codec:h264"));
        rig.Tick();
        Assert.Empty(rig.Actions.Calls);
        Assert.True(rig.Api.Uploads > uploads);
    }

    [Fact]
    public void The_slider_marks_350_and_a_click_near_the_mark_lands_exactly_on_it()
    {
        Assert.Equal(350, HudPanel.SliderValue(HudPanel.SliderX(350)));
        Assert.Equal(350, HudPanel.SliderValue(HudPanel.SliderX(338)));
        Assert.Equal(350, HudPanel.SliderValue(HudPanel.SliderX(364)));
        Assert.Equal(330, HudPanel.SliderValue(HudPanel.SliderX(330)));
        Assert.Equal(375, HudPanel.SliderValue(HudPanel.SliderX(375)));
        using var bitmap = HudPanel.Render(HudView.Empty with { Connected = true, Interactive = true, TargetMbps = 200, RateControl = "vbr", Preset = 2, VbvFrames = 2 }, null);
        var mark = bitmap.GetPixel((int)HudPanel.SliderX(HudPanel.MarkerMbps), 446 - 12);
        Assert.True(mark.R > 200 && mark.G > 150 && mark.B < 130, $"expected an amber mark at 350 Mbps, found {mark}");
    }

    [Fact]
    public void Closing_the_dashboard_drops_an_open_menu_a_held_slider_and_a_repositioning_in_progress()
    {
        var rig = new Rig();
        rig.Tick();
        rig.Press("menu:preset");
        Assert.Equal("preset", rig.Hud.OpenMenu);
        rig.Api.Dashboard = false;
        rig.Tick();
        Assert.Equal("", rig.Hud.OpenMenu);
        rig.Api.Dashboard = true;
        rig.Tick();
        rig.Api.Events.Enqueue(rig.Slide(OverlayEventKind.MouseDown, HudPanel.SliderX(300)));
        rig.Api.Events.Enqueue(rig.Slide(OverlayEventKind.MouseMove, HudPanel.SliderX(450)));
        rig.Tick();
        rig.Api.Dashboard = false;
        rig.Tick(); rig.Tick();
        Assert.Empty(rig.Actions.Calls);                       // the held slider was dropped, not committed
        rig.Api.Dashboard = true;
        rig.Tick(); rig.Press("page:settings"); rig.Press("reposition");
        Assert.True(rig.Hud.Repositioning);
        rig.Api.Dashboard = false;
        rig.Tick();
        Assert.False(rig.Hud.Repositioning);
    }

    [Fact]
    public void Laser_clicks_run_the_matching_actions()
    {
        var rig = new Rig();
        rig.Tick();
        rig.Press("codec:h264"); rig.Press("codec:hevc");
        rig.Press("menu:rc"); rig.Press("rc:cbr");
        rig.Press("menu:preset"); rig.Press("preset:4");
        rig.Press("menu:vbv"); rig.Press("vbv:8");
        Assert.Equal(["codec True", "codec False", "enc cbr P2 V2", "enc vbr P4 V2", "enc vbr P2 V8"], rig.Actions.Calls);
    }

    [Fact]
    public void A_dropdown_replaces_the_controls_with_its_options_and_closes_after_a_choice_or_Close()
    {
        var rig = new Rig();
        rig.Tick();
        rig.Press("menu:preset");
        Assert.Equal("preset", rig.Hud.OpenMenu);
        var ids = HudPanel.Layout(rig.View with { OpenMenu = rig.Hud.OpenMenu }).Select(b => b.Id).ToArray();
        Assert.DoesNotContain("slider", ids);
        Assert.Equal(7, ids.Count(id => id.StartsWith("preset:")));
        rig.Press("menu:close");
        Assert.Equal("", rig.Hud.OpenMenu);
        Assert.Empty(rig.Actions.Calls);
        rig.Press("menu:vbv"); rig.Press("vbv:1");
        Assert.Equal("", rig.Hud.OpenMenu);
    }

    [Fact]
    public void Dragging_the_slider_commits_one_bitrate_on_release()
    {
        var rig = new Rig();
        rig.Tick();
        rig.Api.Events.Enqueue(rig.Slide(OverlayEventKind.MouseDown, HudPanel.SliderX(300)));
        rig.Api.Events.Enqueue(rig.Slide(OverlayEventKind.MouseMove, HudPanel.SliderX(350)));
        rig.Api.Events.Enqueue(rig.Slide(OverlayEventKind.MouseMove, HudPanel.SliderX(400)));
        rig.Tick();
        Assert.Empty(rig.Actions.Calls);                       // nothing is sent while the knob is held
        rig.Api.Events.Enqueue(rig.Slide(OverlayEventKind.MouseUp, HudPanel.SliderX(400)));
        rig.Tick();
        Assert.Equal(["rate 400"], rig.Actions.Calls);
    }

    [Fact]
    public void A_slider_whose_release_never_arrives_commits_when_its_events_stop()
    {
        var rig = new Rig();
        rig.Tick();
        rig.Api.Events.Enqueue(rig.Slide(OverlayEventKind.MouseDown, HudPanel.SliderX(300)));
        rig.Api.Events.Enqueue(rig.Slide(OverlayEventKind.MouseMove, HudPanel.SliderX(450)));
        rig.Tick();
        rig.Tick();
        Assert.Equal(["rate 450"], rig.Actions.Calls);
    }

    [Fact]
    public void The_slider_maps_the_panel_to_whole_steps_within_the_limits()
    {
        Assert.Equal(TrayController.MinimumMbps, HudPanel.SliderValue(-50));
        Assert.Equal(TrayController.MaximumMbps, HudPanel.SliderValue(HudPanel.Width + 50));
        foreach (var x in new[] { 100f, 217f, 333f, 480f }) Assert.Equal(0, HudPanel.SliderValue(x) % HudPanel.BitrateStep);
        // Close to a multiple of 50 the value sits on it, like the tray's slider; between them it moves in 5 Mbps steps.
        foreach (var mbps in new[] { 100, 250, 300, 450, 500 }) Assert.Equal(mbps, HudPanel.SliderValue(HudPanel.SliderX(mbps + 7)));
        Assert.Equal(300, HudPanel.SliderValue(HudPanel.SliderX(293)));
        Assert.Equal(285, HudPanel.SliderValue(HudPanel.SliderX(285)));
        Assert.Equal(TrayController.MinimumMbps, HudPanel.SliderValue(-50));
        Assert.Equal(300, HudPanel.SliderValue(HudPanel.SliderX(300)));
    }

    [Fact]
    public void Moving_with_the_laser_carries_the_panel_with_the_grabbing_hand_then_saves_it()
    {
        var rig = new Rig();
        rig.Api.Poses[3] = Pose.Identity;                          // the carrying (left) controller
        rig.Api.Poses[4] = Pose.Rotation(0, 0, 0, new(1, 0, 0));   // the other hand holding the laser
        rig.Tick();
        rig.Press("page:settings"); rig.Press("reposition");
        Assert.True(rig.Hud.Repositioning);
        // Trigger on the panel body, then the other hand moves 10 cm up.
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseDown, 4, 5, 395));
        rig.Tick();
        rig.Api.Poses[4] = Pose.Rotation(0, 0, 0, new(1, .1f, 0));
        rig.Tick();
        var moved = rig.Api.LastRelative.T - HudSettings.DefaultPose.T;
        Assert.True(Vector3.Distance(moved, new(0, .1f, 0)) < 1e-4f, moved.ToString());
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseUp, 4, 5, 395));
        rig.Tick();
        rig.Press("save");
        Assert.False(rig.Hud.Repositioning);
        Assert.Equal(1, rig.Saves);
        Assert.True(Vector3.Distance(rig.Settings.Pose.T, HudSettings.DefaultPose.T + new Vector3(0, .1f, 0)) < 1e-4f);
    }

    [Fact]
    public void Cancel_throws_the_move_away_and_Default_restores_the_stock_place()
    {
        var rig = new Rig();
        rig.Settings.SetPlacement(Pose.Rotation(0, 0, 0, new(.3f, .3f, .3f)), .3f);
        rig.Tick();
        rig.Press("page:settings"); rig.Press("reposition"); rig.Press("size:+");
        rig.Press("cancel");
        Assert.Equal(.3f, rig.Settings.WidthMeters);
        Assert.Equal(.3f, rig.Api.LastWidth);
        rig.Press("reposition"); rig.Press("default");
        Assert.Equal(HudSettings.DefaultPose, rig.Settings.Pose);
        Assert.Equal(HudSettings.DefaultWidth, rig.Settings.WidthMeters);
        Assert.Equal(1, rig.Saves);
    }

    [Fact]
    public void Grabbing_with_the_carrying_hand_itself_does_nothing()
    {
        var rig = new Rig();
        rig.Api.Poses[3] = Pose.Identity;
        rig.Tick(); rig.Press("page:settings"); rig.Press("reposition");
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseDown, 3, 5, 395)); rig.Tick();
        rig.Api.Poses[3] = Pose.Rotation(0, 0, 0, new(0, 1, 0)); rig.Tick();
        Assert.Equal(HudSettings.DefaultPose, rig.Api.LastRelative);
    }

    [Fact]
    public void A_highlight_follows_the_laser_within_a_tick_but_readouts_are_paced()
    {
        var rig = new Rig();
        var start = new DateTime(2026, 1, 1);
        rig.Hud.Tick(start);
        var uploads = rig.Api.Uploads;
        rig.Api.Events.Enqueue(rig.Click("codec:hevc", OverlayEventKind.MouseMove));
        rig.Hud.Tick(start.AddMilliseconds(40));
        Assert.Equal(uploads + 1, rig.Api.Uploads);
        rig.View = rig.View with { Mbps = 251 };
        rig.Hud.Tick(start.AddMilliseconds(80));
        Assert.Equal(uploads + 1, rig.Api.Uploads);
        rig.Hud.Tick(start.AddMilliseconds(320));
        Assert.Equal(uploads + 2, rig.Api.Uploads);
    }

    [Fact]
    public void An_unchanged_view_is_not_uploaded_again()
    {
        var rig = new Rig();
        rig.Tick(); rig.Tick(); rig.Tick();
        Assert.Equal(1, rig.Api.Uploads);
        rig.View = rig.View with { Mbps = 250 };
        rig.Tick();
        Assert.Equal(2, rig.Api.Uploads);
    }
}

public sealed class OpenVrOverlayApiTests
{
    [Fact]
    public void Without_a_running_SteamVR_it_declines_and_never_starts_one()
    {
        if (System.Diagnostics.Process.GetProcessesByName("vrserver").Length > 0) return; // only meaningful while SteamVR is closed
        using var api = new OpenVrOverlayApi(@"C:\nonexistent");
        Assert.False(api.TryConnect(out var error));
        Assert.Equal("SteamVR is not running.", error);
        Assert.False(System.Diagnostics.Process.GetProcessesByName("vrserver").Length > 0);
    }
}

public sealed class HudProtocolTests
{
    [Fact]
    public void Views_and_settings_survive_a_trip_through_a_line()
    {
        var settings = new HudSettings { Enabled = true, Hand = "right" };
        settings.SetPlacement(Pose.Rotation(0, -30, 0, new(0, .1f, 0)), .3f);
        var view = HudView.Empty with { Connected = true, Codec = "H.264", Mbps = 312, TargetMbps = 300 };
        var line = new HudMessage { Type = "view", View = view, Settings = settings }.ToLine();
        Assert.DoesNotContain('\n', line);
        var back = HudMessage.Parse(line)!;
        Assert.Equal("view", back.Type);
        Assert.Equal(view, back.View);
        Assert.Equal("right", back.Settings!.Hand);
        Assert.Equal(settings.Placement, back.Settings.Placement);
    }

    [Fact]
    public void Garbage_is_ignored_not_thrown()
    {
        Assert.Null(HudMessage.Parse("{ nope"));
    }
}

public sealed class GpuOverlayTextureTests
{
    [Fact]
    public void A_texture_is_created_and_rewritten_in_place_on_a_machine_with_a_GPU()
    {
        using var texture = GpuOverlayTexture.TryCreate(HudPanel.Width, HudPanel.Height);
        if (texture is null) return; // no Direct3D 11 hardware here
        Assert.NotEqual(0, texture.Texture);
        var first = texture.Texture;
        using var bitmap = HudPanel.Render(HudView.Empty, null);
        var pixels = HudPanel.ToRgba(bitmap);
        texture.Update(pixels);
        texture.Update(pixels);
        Assert.Equal(first, texture.Texture);
    }

    [Fact]
    public void A_notice_is_drawn_on_the_panel_in_both_states()
    {
        foreach (var interactive in new[] { true, false })
        {
            var plain = HudView.Empty with { Interactive = interactive };
            var noticed = plain with { Notice = "H.264 needs the SteamVR driver. Close SteamVR, then choose H.264 again." };
            using var a = HudPanel.Render(plain, null);
            using var b = HudPanel.Render(noticed, null);
            var differs = false;
            for (var y = 200; y < 600 && !differs; y += 4)
                for (var x = 30; x < 600 && !differs; x += 4) differs = a.GetPixel(x, y) != b.GetPixel(x, y);
            Assert.True(differs);
        }
    }
}
