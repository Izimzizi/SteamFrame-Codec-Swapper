using SteamFrameTray;
using SteamFrame.Core;

namespace TrayTests;

public sealed class BitrateSyncTests
{
    private static CodecServiceStatus Live(string state = "ready", int target = 300, bool rateLive = true) =>
        new() { Live = true, State = state, ActiveCodec = "H.264", Keep = true, TargetMbps = target, RateLive = rateLive, Message = state };

    private static TrayController Make(out FakeCodec codec, out FakeLive live, out FramePreferences prefs)
    {
        codec = new FakeCodec(); live = new FakeLive(); prefs = new FramePreferences { CodecTargetMbps = 300 };
        return new TrayController(codec, new FakeVault(), new FakePatch(), prefs, () => Task.CompletedTask, live);
    }

    private static Task<ChangeOutcome> Change(TrayController controller, int mbps) =>
        controller.ChangeTargetAsync(mbps, () => Task.FromResult<string?>(null), _ => Task.FromResult(true), () => Task.FromResult(true));

    [Fact]
    public async Task Disarmed_change_is_written_live_when_SteamVR_runs()
    {
        var controller = Make(out var codec, out var live, out var prefs);
        Assert.Equal(ChangeOutcome.AppliedLive, await Change(controller, 280));
        Assert.Equal([280], live.Applied); Assert.Empty(live.FileWrites); Assert.Empty(codec.Rates);
        Assert.Equal(280, prefs.CodecTargetMbps); Assert.Equal(280, controller.TargetMbps);
    }

    [Fact]
    public async Task Disarmed_change_goes_to_the_settings_file_when_SteamVR_is_closed()
    {
        var controller = Make(out _, out var live, out _);
        live.SteamVrRunning = false;
        Assert.Equal(ChangeOutcome.Saved, await Change(controller, 310));
        Assert.Equal([310], live.FileWrites); Assert.Empty(live.Applied);
    }

    [Fact]
    public async Task A_live_write_failure_is_reported_but_the_value_is_kept()
    {
        var controller = Make(out _, out var live, out var prefs);
        live.Error = new IOException("boom");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Change(controller, 280));
        Assert.Contains("saved", error.Message); Assert.Equal(280, prefs.CodecTargetMbps);
    }

    [Fact]
    public async Task A_SteamVR_slider_move_is_adopted_when_disarmed()
    {
        var controller = Make(out var codec, out var live, out var prefs);
        live.Target = 300; await controller.RefreshAsync();
        live.Target = 345; await controller.RefreshAsync();
        Assert.Equal(345, prefs.CodecTargetMbps); Assert.Equal(345, controller.Current.TargetMbps);
        Assert.Empty(codec.Rates);
        await controller.RefreshAsync();
        Assert.Equal(345, prefs.CodecTargetMbps);
    }

    [Fact]
    public async Task A_stale_reading_after_our_own_write_does_not_undo_it()
    {
        var controller = Make(out _, out var live, out var prefs);
        live.Target = 300; await controller.RefreshAsync();
        await Change(controller, 280);
        live.Target = 300;
        await controller.RefreshAsync();
        Assert.Equal(280, prefs.CodecTargetMbps);
        live.Target = 280; await controller.RefreshAsync();
        Assert.Equal(280, prefs.CodecTargetMbps);
    }

    [Fact]
    public async Task A_SteamVR_move_while_armed_is_sent_to_the_supervisor_without_writing_back()
    {
        var controller = Make(out var codec, out var live, out var prefs);
        codec.Status = Live();
        live.Target = 300; await controller.RefreshAsync();
        live.Target = 360; await controller.RefreshAsync();
        Assert.Equal([360], codec.Rates); Assert.Empty(live.Applied);
        Assert.Equal(360, prefs.CodecTargetMbps); Assert.Equal(360, controller.Current.TargetMbps);
        await controller.RefreshAsync();
        Assert.Equal([360], codec.Rates);
    }

    [Fact]
    public async Task SteamVR_moves_are_ignored_for_non_live_sessions()
    {
        var controller = Make(out var codec, out var live, out var prefs);
        live.Target = 300; await controller.RefreshAsync();

        codec.Status = Live(rateLive: false);
        live.Target = 380; await controller.RefreshAsync();
        Assert.Empty(codec.Rates); Assert.Equal(300, prefs.CodecTargetMbps);
    }

    [Fact]
    public async Task Readings_outside_the_supported_range_are_ignored()
    {
        var controller = Make(out _, out var live, out var prefs);
        live.Target = 300; await controller.RefreshAsync();
        live.Target = 10; await controller.RefreshAsync();
        live.Target = double.NaN; await controller.RefreshAsync();
        Assert.Equal(300, prefs.CodecTargetMbps);
    }
}

public sealed class SettingsFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vrsettings-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "steamvr.vrsettings");
    public SettingsFileTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Write_sets_the_target_and_keeps_every_other_key()
    {
        File.WriteAllText(File_, "{\n  \"steamvr\": {\"x\": 1},\n  \"driver_vrlink\": {\"automaticBandwidth\": false, \"targetBandwidth\": 200, \"other\": \"keep\"}\n}");
        LiveBitrate.WriteFileTarget(File_, 333);
        Assert.Equal(333, LiveBitrate.ReadFileTarget(File_));
        var text = File.ReadAllText(File_);
        Assert.Contains("\"other\": \"keep\"", text); Assert.Contains("\"automaticBandwidth\": false", text); Assert.Contains("\"x\": 1", text);
        Assert.Contains("\"recommendedBandwidthMbit\": 333", text);
        Assert.Empty(Directory.GetFiles(_dir, "*.tray-*"));
    }

    [Fact]
    public void Write_creates_the_section_or_file_when_missing()
    {
        LiveBitrate.WriteFileTarget(File_, 250);
        Assert.Equal(250, LiveBitrate.ReadFileTarget(File_));
    }

    [Fact]
    public void Unreadable_files_are_reported_and_left_unchanged()
    {
        File.WriteAllText(File_, "{ not json");
        Assert.Throws<IOException>(() => LiveBitrate.WriteFileTarget(File_, 250));
        Assert.Equal("{ not json", File.ReadAllText(File_));
        Assert.Null(LiveBitrate.ReadFileTarget(File_));
    }

    [Fact]
    public void Reading_handles_a_byte_order_mark_and_a_missing_file()
    {
        File.WriteAllText(File_, "﻿{\"driver_vrlink\": {\"targetBandwidth\": 350.0}}");
        Assert.Equal(350, LiveBitrate.ReadFileTarget(File_));
        Assert.Null(LiveBitrate.ReadFileTarget(Path.Combine(_dir, "missing")));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
}
