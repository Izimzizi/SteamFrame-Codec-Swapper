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
    public void The_default_placements_are_the_ones_chosen_on_a_headset_with_the_panel_beyond_the_icon_from_the_controller()
    {
        Assert.True(HudSettings.DefaultPose.T.Length() > .1f && HudSettings.DefaultIconPose.T.Length() > .1f);
        Assert.True(Vector3.Distance(HudSettings.DefaultPose.T, HudSettings.DefaultIconPose.T) > .05f);
        Assert.NotEqual(Pose.Identity, HudSettings.DefaultPose);
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

    [Fact]
    public void A_ray_meets_a_flat_overlay_at_the_pixel_it_points_at()
    {
        // A 0.2 m wide, 100 x 50 pixel overlay at the origin facing +Z; the ray starts 1 m in front of it and looks down -Z.
        var overlay = Pose.Identity;
        Assert.True(HudAim.TryHit(new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new(0, 0, 1)), overlay, .2f, 100, 50, 0, out var x, out var y, out var distance, out var inside, out _));
        Assert.True(inside); Assert.Equal(50f, x, 2); Assert.Equal(25f, y, 2); Assert.Equal(1f, distance, 3);
        // 5 cm to the right and 2.5 cm up: three quarters across, three quarters up (origin bottom-left).
        Assert.True(HudAim.TryHit(new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new(.05f, .025f, 1)), overlay, .2f, 100, 50, 0, out x, out y, out _, out inside, out _));
        Assert.Equal(75f, x, 2); Assert.Equal(37.5f, y, 2);
    }

    [Fact]
    public void A_ray_outside_the_edge_only_counts_inside_the_margin_and_never_from_behind()
    {
        var facing = new Pose(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new(.11f, 0, 1));
        Assert.False(HudAim.TryHit(facing, Pose.Identity, .2f, 100, 50, 0, out _, out _, out _, out _, out _));
        Assert.True(HudAim.TryHit(facing, Pose.Identity, .2f, 100, 50, HudAim.NearMargin, out var x, out _, out _, out var inside, out _));
        Assert.False(inside); Assert.Equal(100f, x, 2);   // clamped to the picture
        var behind = new Pose(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new(0, 0, -1));   // looks away from the overlay
        Assert.False(HudAim.TryHit(behind, Pose.Identity, .2f, 100, 50, HudAim.NearMargin, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void The_laser_strip_runs_along_the_ray_and_turns_toward_the_viewer()
    {
        var line = HudAim.Line(Vector3.Zero, new(1, 0, 0), new(.5f, 1, 0), out var length);
        Assert.Equal(1f, length, 3);
        Near(line.X, Vector3.UnitX); Near(line.Z, Vector3.UnitY); Near(line.T, new(.5f, 0, 0));
        Near(Vector3.Cross(line.X, line.Y), line.Z);   // a proper rotation
    }
}

public sealed class HudInputManifestTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hud-input-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_manifest_names_the_aim_and_click_actions_and_binds_them_for_the_frames_controller_and_the_common_ones()
    {
        var path = HudInputManifest.Write(_directory, "system.generated.codecswapper.exe");
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var actions = manifest.RootElement.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToArray();
        Assert.Equal([HudInputManifest.TipAction, HudInputManifest.ClickAction], actions);
        var bindings = manifest.RootElement.GetProperty("default_bindings").EnumerateArray().ToArray();
        Assert.Contains(bindings, b => b.GetProperty("controller_type").GetString() == "frame_controller");
        foreach (var binding in bindings)
        {
            using var file = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, binding.GetProperty("binding_url").GetString()!)));
            var root = file.RootElement.GetProperty("bindings");
            Assert.Equal(2, root.GetProperty(HudInputManifest.AimSet).GetProperty("poses").GetArrayLength());
            Assert.Equal(2, root.GetProperty(HudInputManifest.ClickSet).GetProperty("sources").GetArrayLength());
        }
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }
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
    private static HudView View(bool reposition = false, bool busy = false) => HudView.Empty with { Connected = true, TargetMbps = 300, WidthPx = 1344, Repositioning = reposition, Busy = busy };

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
        public readonly Dictionary<ulong, float> Alpha = [];
        public void SetAlpha(ulong overlay, float alpha) => Alpha[overlay] = alpha;
        public void EnableLaser(ulong overlay, int w, int h) => Calls.Add($"laser {w}x{h}");
        public void SetImage(ulong overlay, byte[] rgba, int width, int height) => Uploads++;
        public void SetVisible(ulong overlay, bool visible) { Calls.Add(visible ? "show" : "hide"); Visible[overlay] = visible; }
        public uint ControllerIndex(bool left) => left ? Left : Right;
        public void AttachToDevice(ulong overlay, uint device, Pose relative) { LastDevice = device; LastRelative = relative; }
        public bool TryDevicePose(uint device, out Pose pose) => Poses.TryGetValue(device, out pose);
        public IReadOnlyList<OverlayEvent> PollEvents(ulong overlay) { var queue = overlay == 7 ? Events : IconEvents; var list = queue.ToList(); queue.Clear(); return list; }
        public bool DashboardVisible() => Dashboard;
        public bool InputOn, AimOk = true, Trigger, LastClaim;
        public Pose Tip = Pose.Identity;
        public readonly List<string> Laser = [];
        public bool InputAvailable => InputOn;
        public bool TryAim(bool left, bool claimClick, out Pose tip, out bool pressed) { tip = Tip; LastClaim = claimClick; pressed = claimClick && Trigger; return AimOk; }
        public void ShowLaser(Pose line, float length, Pose dot) => Laser.Add("show");
        public void HideLaser() => Laser.Add("hide");
        public void Dispose() { }
    }

    private sealed class Actions : IHudActions
    {
        public readonly List<string> Calls = [];
        public Task SetCodecAsync(bool h264) { Calls.Add("codec " + h264); return Task.CompletedTask; }
        public Task SetBitrateAsync(int mbps) { Calls.Add("rate " + mbps); return Task.CompletedTask; }
        public Task SetWidthAsync(int width) { Calls.Add("width " + width); return Task.CompletedTask; }
        public Task SetEncoderAsync(string rc, int preset, int vbv) { Calls.Add($"enc {rc} P{preset} V{vbv}"); return Task.CompletedTask; }
        public Task SetMultiPassAsync(int mode) { Calls.Add("multipass " + mode); return Task.CompletedTask; }
        public Task HideAsync() { Calls.Add("hud off"); return Task.CompletedTask; }
        public Task ResetCountersAsync() { Calls.Add("reset counters"); return Task.CompletedTask; }
    }

    private sealed class Rig
    {
        public readonly FakeApi Api = new();
        public readonly Actions Actions = new();
        public readonly HudSettings Settings = new() { Enabled = true, Watch = false, GuideDone = true };
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

    // Aim the pointing hand at a button of the panel, from 30 cm in front of it.
    private static Pose AimAt(Rig rig, string id)
    {
        var b = HudPanel.Layout(rig.View with { Repositioning = rig.Hud.Repositioning, OpenMenu = rig.Hud.OpenMenu, Page = rig.Hud.Page, Interactive = true }).Single(x => x.Id == id).Bounds;
        var across = ((b.X + b.Width / 2f) / HudPanel.Width - .5f) * rig.Settings.WidthMeters;
        var up = ((HudPanel.Height - (b.Y + b.Height / 2f)) / HudPanel.Height - .5f) * rig.Settings.WidthMeters * HudPanel.Height / HudPanel.Width;
        var world = Pose.Identity.Then(rig.Settings.Pose);
        var point = world.T + world.X * across + world.Y * up;
        return new(world.X, world.Y, world.Z, point + world.Z * .3f);
    }

    private static Rig OwnLaserRig()
    {
        var rig = new Rig();
        rig.Api.Dashboard = false; rig.Api.InputOn = true;
        rig.Api.Poses[3] = Pose.Identity; rig.Api.Poses[0] = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new(0, 0, .6f));
        rig.Tick();
        return rig;
    }

    [Fact]
    public void With_the_dashboard_closed_the_own_laser_hovers_and_the_trigger_presses_a_button()
    {
        var rig = OwnLaserRig();
        rig.Api.Tip = AimAt(rig, "codec:h264");
        rig.Tick();
        Assert.Contains("show", rig.Api.Laser);
        Assert.Empty(rig.Actions.Calls);
        rig.Api.Trigger = true; rig.Tick();      // aimed at the HUD since the last tick: the trigger is taken from the game and pressed
        Assert.True(rig.Api.LastClaim);
        Assert.Contains("codec True", rig.Actions.Calls);
    }

    [Fact]
    public void The_own_laser_leaves_the_trigger_to_the_game_until_it_is_on_the_hud_and_hides_when_steamvrs_own_laser_takes_over()
    {
        var rig = OwnLaserRig();
        rig.Api.Tip = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new(5, 5, 5));   // far away, looking down -Z
        rig.Api.Trigger = true; rig.Tick();
        Assert.False(rig.Api.LastClaim);
        Assert.Empty(rig.Actions.Calls);
        rig.Api.Tip = AimAt(rig, "codec:h264"); rig.Tick();
        rig.Api.Laser.Clear();
        rig.Api.Dashboard = true;                // reported open all the time on a Steam Link session: this alone changes nothing
        rig.Tick();
        Assert.Contains("show", rig.Api.Laser);
        rig.Api.Laser.Clear();
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseMove, 4, 10, 10)); rig.Tick();    // SteamVR's own laser reaches the HUD...
        rig.Api.Laser.Clear(); rig.Tick();   // ...and ours steps aside from the next tick on
        Assert.Contains("hide", rig.Api.Laser);
        Assert.DoesNotContain("show", rig.Api.Laser);
    }

    [Fact]
    public void The_own_laser_is_off_without_input_or_when_switched_off()
    {
        var rig = OwnLaserRig();
        rig.Api.Tip = AimAt(rig, "codec:h264");
        rig.Settings.OwnLaser = false; rig.Tick();
        Assert.DoesNotContain("show", rig.Api.Laser);
        rig.Settings.OwnLaser = true; rig.Api.InputOn = false; rig.Tick();
        Assert.DoesNotContain("show", rig.Api.Laser);
    }

    [Fact]
    public void In_watch_style_the_panel_waits_behind_a_round_icon_that_opens_and_closes_it()
    {
        var rig = new Rig();
        rig.Settings.Watch = true;
        rig.Tick();
        Assert.False(rig.Api.Visible[7]);
        Assert.True(rig.Api.Visible[8]);
        Assert.Equal(HudSettings.DefaultIconPose, rig.Api.LastRelative);
        rig.Api.IconEvents.Enqueue(new(OverlayEventKind.MouseDown, 4, 60, 60)); rig.Tick();
        Assert.True(rig.Api.Visible[7]);
        Assert.True(rig.Settings.PanelOpen);
        rig.Api.IconEvents.Enqueue(new(OverlayEventKind.MouseDown, 4, 60, 60)); rig.Tick();
        Assert.False(rig.Api.Visible[7]);
    }

    [Fact]
    public void The_first_run_guide_starts_with_a_bubble_at_the_watch_then_walks_the_panel_and_is_remembered()
    {
        var rig = new Rig();
        rig.Settings.Watch = true; rig.Settings.GuideDone = false;
        rig.Tick();
        Assert.Equal(3, rig.Api.Calls.Count(c => c == "create"));   // panel, watch, and the bubble pointing at the watch
        Assert.True(rig.Api.Visible[9]);
        rig.Api.IconEvents.Enqueue(new(OverlayEventKind.MouseDown, 4, 60, 60)); rig.Tick();   // clicking the watch opens the panel, the bubble goes and step 1 begins
        Assert.True(rig.Settings.PanelOpen);
        Assert.Contains("destroy", rig.Api.Calls);
        var guided = rig.View with { Guide = 1, Interactive = true };
        for (var step = 1; step < HudPanel.GuideSteps; step++)
        {
            rig.Api.Events.Enqueue(Press(guided with { Guide = step }, "guide:next")); rig.Tick();
        }
        Assert.False(rig.Settings.GuideDone);
        rig.Api.Events.Enqueue(Press(guided with { Guide = HudPanel.GuideSteps }, "guide:next")); rig.Tick();   // Finish
        Assert.True(rig.Settings.GuideDone);
        Assert.True(rig.Saves > 0);
        rig.Press("page:settings"); rig.Press("guide:replay");
        Assert.Equal("", rig.Hud.Page);
        rig.Api.Events.Enqueue(Press(guided, "guide:skip")); rig.Tick();
        Assert.True(rig.Settings.GuideDone);
    }

    private static OverlayEvent Press(HudView view, string id)
    {
        var b = HudPanel.Layout(view).Single(x => x.Id == id).Bounds;
        return new(OverlayEventKind.MouseDown, 4, b.X + b.Width / 2f, HudPanel.Height - (b.Y + b.Height / 2f));
    }

    [Fact]
    public void During_the_guide_only_its_buttons_work_and_the_picture_draws()
    {
        var view = HudView.Empty with { Connected = true, TargetMbps = 300, WidthPx = 1344, Guide = 3 };
        Assert.Equal(["guide:next", "guide:skip"], HudPanel.Layout(view).Select(b => b.Id).ToArray());
        Assert.Equal("grab", HudPanel.HitTest(view, 100, 420));   // the slider under the dimming is not reachable
        for (var step = 1; step <= HudPanel.GuideSteps; step++)
        {
            using var picture = HudPanel.Render(view with { Guide = step }, "guide:next");
            Assert.Equal(HudPanel.Width, picture.Width);
            var callout = HudPanel.GuideStep(step).Callout;
            Assert.All(HudPanel.GuideLayout(view with { Guide = step }), b => Assert.True(callout.Contains(b.Bounds)));
        }
        using var bubble = HudPanel.RenderWatchGuide();
        Assert.Equal(HudPanel.GuideBubbleWidth, bubble.Width);
    }

    [Fact]
    public void An_opened_watch_panel_is_fully_active_without_the_laser_or_the_dashboard()
    {
        var rig = new Rig();
        rig.Settings.Watch = true; rig.Settings.PanelOpen = true; rig.Api.Dashboard = false;
        rig.Tick(); rig.Tick(); rig.Tick();
        rig.Press("page:settings");
        Assert.Equal("settings", rig.Hud.Page);
        rig.Tick(); rig.Tick();
        Assert.Equal("settings", rig.Hud.Page);   // no laser movement, still there
        rig.Press("reposition");
        rig.Tick(); rig.Tick();
        Assert.True(rig.Hud.Repositioning);
    }

    [Fact]
    public void The_watch_gauge_follows_the_readings_at_once_not_every_few_seconds()
    {
        var rig = new Rig();
        rig.Settings.Watch = true;
        rig.Tick();
        var before = rig.Api.Uploads;
        rig.Tick();
        Assert.Equal(before, rig.Api.Uploads);                       // nothing changed
        rig.View = rig.View with { Mbps = 120 }; rig.Tick();
        Assert.Equal(before + 1, rig.Api.Uploads);
        rig.View = rig.View with { Mbps = 180 }; rig.Tick();
        Assert.Equal(before + 2, rig.Api.Uploads);
    }

    [Fact]
    public void The_reset_counters_button_asks_the_tray_to_start_the_frame_counts_again()
    {
        var rig = new Rig();
        rig.Tick();
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseMove, 4, 100, 100));
        rig.Press("counters");
        Assert.Contains("reset counters", rig.Actions.Calls);
    }

    [Fact]
    public void The_HUD_and_the_watch_each_fade_out_when_they_face_away_from_the_viewer()
    {
        var rig = new Rig();
        rig.Settings.Watch = true; rig.Settings.PanelOpen = true; rig.Settings.FadeAngle = 60;
        rig.Settings.SetPlacement(Pose.Identity, .2f); rig.Settings.SetIconPlacement(Pose.Identity, .05f);   // both face +Z, the way the hand faces
        rig.Api.Poses[0] = Pose.Identity;                                   // the headset at the origin
        rig.Api.Poses[3] = Pose.Rotation(0, 0, 0, new(0, 0, -.5f));         // the hand in front of it, facing back at it
        rig.Tick();
        Assert.Equal(1f, rig.Api.Alpha[7]);
        Assert.Equal(1f, rig.Api.Alpha[8]);
        rig.Api.Poses[3] = Pose.Rotation(180, 0, 0, new(0, 0, -.5f));      // the hand turns its back to the headset
        rig.Tick(); rig.Tick();
        Assert.Equal(0f, rig.Api.Alpha[7]);
        Assert.Equal(0f, rig.Api.Alpha[8]);
    }

    [Fact]
    public void The_fade_out_sliders_take_effect_while_held_and_are_saved_on_release()
    {
        var rig = new Rig();
        rig.Tick(); rig.Press("page:settings");
        var saved = rig.Saves;
        float Y(System.Drawing.Rectangle area) => HudPanel.Height - (area.Top + 46);
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseDown, 4, HudPanel.ChoiceX(HudPanel.FadeAngleChoices, 30), Y(HudPanel.FadeAngleArea))); rig.Tick();
        Assert.Equal(30, rig.Settings.FadeAngle);
        Assert.Equal(saved, rig.Saves);
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseUp, 4, 0, 0)); rig.Tick();
        Assert.Equal(saved + 1, rig.Saves);
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseDown, 4, 100, Y(HudPanel.FadeDistanceArea)));
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseMove, 4, HudPanel.ChoiceX(HudPanel.FadeDistanceChoices, 60), Y(HudPanel.FadeDistanceArea)));
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseUp, 4, 0, 0)); rig.Tick();
        Assert.Equal(60, rig.Settings.FadeDistanceCm);
    }

    [Fact]
    public void The_HUD_also_fades_out_beyond_the_chosen_distance_and_stays_when_fading_is_off()
    {
        var rig = new Rig();
        rig.Settings.FadeDistanceCm = 35;
        rig.Settings.SetPlacement(Pose.Identity, .2f);
        rig.Api.Poses[0] = Pose.Identity;
        rig.Api.Poses[3] = Pose.Rotation(0, 0, 0, new(0, 0, -.5f));
        rig.Tick(); rig.Tick();
        Assert.Equal(0f, rig.Api.Alpha[7]);
        rig.Settings.FadeDistanceCm = 0;
        rig.Tick(); rig.Tick();
        Assert.Equal(1f, rig.Api.Alpha[7]);
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
        rig.Press("page:settings");
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseMove, 4, 100, 100));
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
    public void The_multipass_dropdown_offers_off_and_both_two_pass_modes_and_sends_the_choice()
    {
        var rig = new Rig();
        rig.Tick();
        rig.Press("menu:mp");
        Assert.Equal("mp", rig.Hud.OpenMenu);
        Assert.Equal(["mp:0", "mp:1", "mp:2"], HudPanel.Layout(rig.View with { OpenMenu = rig.Hud.OpenMenu }).Select(b => b.Id).Where(id => id.StartsWith("mp:")).ToArray());
        rig.Press("mp:2");
        Assert.Equal("", rig.Hud.OpenMenu);
        Assert.Equal(["multipass 2"], rig.Actions.Calls);
        Assert.Equal("Multipass", HudPanel.MenuTitle("mp"));
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
    public void Dragging_the_width_slider_commits_one_width_on_release_and_is_inactive_while_the_width_is_unknown()
    {
        var rig = new Rig();
        rig.Tick();
        Assert.False(HudPanel.Layout(rig.View).Single(x => x.Id == "wslider").Enabled);   // width 0: not read yet
        rig.View = rig.View with { WidthPx = 1344 };
        OverlayEvent At(OverlayEventKind kind, int width) => new(kind, 4, HudPanel.ChoiceX(HudPanel.WidthChoices, width), HudPanel.Height - (HudPanel.WidthArea.Top + 46));
        rig.Api.Events.Enqueue(At(OverlayEventKind.MouseDown, 1344));
        rig.Api.Events.Enqueue(At(OverlayEventKind.MouseMove, 1700));
        rig.Api.Events.Enqueue(At(OverlayEventKind.MouseMove, 2048));
        rig.Tick();
        Assert.Empty(rig.Actions.Calls);
        rig.Api.Events.Enqueue(At(OverlayEventKind.MouseUp, 2048));
        rig.Tick();
        Assert.Equal(["width 2048"], rig.Actions.Calls);
    }

    [Fact]
    public void The_width_slider_covers_the_tray_sliders_range_in_steps_of_32()
    {
        Assert.Equal(StreamWidth.Minimum, HudPanel.WidthChoices[0]);
        Assert.Equal(StreamWidth.Maximum, HudPanel.WidthChoices[^1]);
        Assert.All(HudPanel.WidthChoices.Zip(HudPanel.WidthChoices.Skip(1)), pair => Assert.Equal(StreamWidth.Step, pair.Second - pair.First));
        Assert.Equal(StreamWidth.Maximum, HudPanel.ChoiceAt(HudPanel.WidthChoices, HudPanel.Width + 50));
        Assert.Equal(1920, HudPanel.ChoiceAt(HudPanel.WidthChoices, HudPanel.ChoiceX(HudPanel.WidthChoices, 1920)));
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
    public void Reposition_also_moves_and_resizes_the_watch_icon_and_saves_both()
    {
        var rig = new Rig();
        rig.Settings.Watch = true; rig.Settings.PanelOpen = true;
        rig.Api.Poses[3] = Pose.Identity;
        rig.Api.Poses[4] = Pose.Rotation(0, 0, 0, new(1, 0, 0));
        rig.Tick();
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseMove, 4, 100, 100)); rig.Press("page:settings");
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseMove, 4, 100, 100)); rig.Press("reposition");
        Assert.True(rig.Hud.Repositioning);
        // Trigger on the icon (not the panel), then the other hand moves 5 cm up: only the icon follows.
        rig.Api.IconEvents.Enqueue(new(OverlayEventKind.MouseDown, 4, 60, 60)); rig.Tick();
        Assert.True(rig.Settings.PanelOpen);
        rig.Api.Poses[4] = Pose.Rotation(0, 0, 0, new(1, .05f, 0)); rig.Tick();
        Assert.True(Vector3.Distance(rig.Api.LastRelative.T, HudSettings.DefaultIconPose.T + new Vector3(0, .05f, 0)) < 1e-4f, rig.Api.LastRelative.T.ToString());
        rig.Api.IconEvents.Enqueue(new(OverlayEventKind.MouseUp, 4, 60, 60)); rig.Tick();
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseMove, 4, 100, 100)); rig.Press("size:+");
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseMove, 4, 100, 100)); rig.Press("save");
        Assert.False(rig.Hud.Repositioning);
        Assert.True(rig.Settings.IconWidthMeters > HudSettings.DefaultIconWidth);
        Assert.Equal(HudSettings.DefaultWidth, rig.Settings.WidthMeters);
        Assert.True(Vector3.Distance(rig.Settings.IconPose.T, HudSettings.DefaultIconPose.T + new Vector3(0, .05f, 0)) < 1e-4f);
        Assert.Equal(HudSettings.DefaultPose, rig.Settings.Pose);
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
