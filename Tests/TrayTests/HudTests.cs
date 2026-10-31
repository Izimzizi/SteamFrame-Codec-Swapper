using System.Numerics;
using SteamFrameTray;

namespace TrayTests;

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
    public void Tilting_back_by_the_default_pitch_turns_the_panel_toward_up_and_back()
    {
        var normal = HudSettings.DefaultPose.Rotate(Vector3.UnitZ);
        Assert.True(normal.Y > .7f && normal.Z > .6f, normal.ToString());
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
        Assert.False(HudSettings.Load(_file).Enabled);
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
        foreach (var view in new[] { View(), View(reposition: true) })
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
        public bool Running = true, CanConnect = true;
        public uint Left = 3, Right = 4;
        public readonly List<string> Calls = [];
        public readonly Queue<OverlayEvent> Events = new();
        public readonly Dictionary<uint, Pose> Poses = [];
        public Pose LastRelative; public uint LastDevice = IOverlayApi.NoDevice; public float LastWidth; public int Uploads;
        public bool TryConnect(out string error) { error = CanConnect ? "" : "no SteamVR"; if (CanConnect) { Connected = true; Calls.Add("connect"); } return CanConnect; }
        public void Disconnect() { Connected = false; Calls.Add("disconnect"); }
        public bool StillRunning() => Running;
        public ulong CreateOverlay(string key, string name) { Calls.Add("create"); return 7; }
        public void DestroyOverlay(ulong overlay) => Calls.Add("destroy");
        public void SetWidth(ulong overlay, float meters) => LastWidth = meters;
        public void SetAlpha(ulong overlay, float alpha) { }
        public void EnableLaser(ulong overlay, int w, int h) => Calls.Add($"laser {w}x{h}");
        public void SetImage(ulong overlay, byte[] rgba, int width, int height) => Uploads++;
        public void SetVisible(ulong overlay, bool visible) => Calls.Add(visible ? "show" : "hide");
        public uint ControllerIndex(bool left) => left ? Left : Right;
        public void AttachToDevice(ulong overlay, uint device, Pose relative) { LastDevice = device; LastRelative = relative; }
        public bool TryDevicePose(uint device, out Pose pose) => Poses.TryGetValue(device, out pose);
        public IReadOnlyList<OverlayEvent> PollEvents(ulong overlay) { var list = Events.ToList(); Events.Clear(); return list; }
        public void Dispose() { }
    }

    private sealed class Actions : IHudActions
    {
        public readonly List<string> Calls = [];
        public Task SetCodecAsync(bool h264) { Calls.Add("codec " + h264); return Task.CompletedTask; }
        public Task SetBitrateAsync(int mbps) { Calls.Add("rate " + mbps); return Task.CompletedTask; }
        public Task SetEncoderAsync(string rc, int preset, int vbv) { Calls.Add($"enc {rc} P{preset} V{vbv}"); return Task.CompletedTask; }
    }

    private sealed class Rig
    {
        public readonly FakeApi Api = new();
        public readonly Actions Actions = new();
        public readonly HudSettings Settings = new() { Enabled = true };
        public int Saves; public bool SteamVr = true;
        public HudView View = HudView.Empty with { Connected = true, TargetMbps = 300, RateControl = "vbr", Preset = 2, VbvFrames = 2 };
        public readonly HudController Hud;
        private DateTime _clock = new(2026, 1, 1);
        public Rig() => Hud = new(Api, Settings, () => View, Actions, () => Saves++, () => SteamVr);
        public void Tick() { _clock += TimeSpan.FromSeconds(1); Hud.Tick(_clock); }
        // Laser click at a button's centre (GL space: y counts up from the bottom).
        public OverlayEvent Click(string id, OverlayEventKind kind = OverlayEventKind.MouseDown, uint device = 4)
        {
            var b = HudPanel.Layout(View with { Repositioning = Hud.Repositioning }).Single(x => x.Id == id).Bounds;
            return new(kind, device, b.X + b.Width / 2f, HudPanel.Height - (b.Y + b.Height / 2f));
        }
        public void Press(string id) { Api.Events.Enqueue(Click(id)); Tick(); }
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
        Assert.Equal(["connect", "create", "laser 512x400", "show"], rig.Api.Calls);
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
    public void Laser_clicks_run_the_matching_actions()
    {
        var rig = new Rig();
        rig.Tick();
        rig.Press("codec:h264"); rig.Press("codec:hevc");
        rig.Press("rate:+"); rig.Press("rate:-");
        rig.Press("rc:cbr"); rig.Press("preset:4"); rig.Press("vbv:8");
        Assert.Equal(["codec True", "codec False", "rate 310", "rate 290", "enc cbr P2 V2", "enc vbr P4 V2", "enc vbr P2 V8"], rig.Actions.Calls);
    }

    [Fact]
    public void The_bitrate_buttons_stop_at_the_limits()
    {
        var rig = new Rig();
        rig.View = rig.View with { TargetMbps = TrayController.MaximumMbps };
        rig.Tick(); rig.Press("rate:+");
        Assert.Empty(rig.Actions.Calls);
    }

    [Fact]
    public void Moving_with_the_laser_carries_the_panel_with_the_grabbing_hand_then_saves_it()
    {
        var rig = new Rig();
        rig.Api.Poses[3] = Pose.Identity;                          // the carrying (left) controller
        rig.Api.Poses[4] = Pose.Rotation(0, 0, 0, new(1, 0, 0));   // the other hand holding the laser
        rig.Tick();
        rig.Press("reposition");
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
        rig.Press("reposition"); rig.Press("size:+");
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
        rig.Tick(); rig.Press("reposition");
        rig.Api.Events.Enqueue(new(OverlayEventKind.MouseDown, 3, 5, 395)); rig.Tick();
        rig.Api.Poses[3] = Pose.Rotation(0, 0, 0, new(0, 1, 0)); rig.Tick();
        Assert.Equal(HudSettings.DefaultPose, rig.Api.LastRelative);
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
