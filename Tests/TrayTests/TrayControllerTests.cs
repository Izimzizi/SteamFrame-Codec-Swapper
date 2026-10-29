using SteamFrameTray;
using SteamFrame.Core;

namespace TrayTests;

internal sealed class FakeCodec : ICodecService
{
    public string SessionDirectory { get; } = Path.Combine(Path.GetTempPath(), "tray-tests-" + Guid.NewGuid().ToString("N"));
    public CodecServiceStatus Status { get; set; } = CodecServiceStatus.NotStarted;
    public CodecServiceStatus? AfterSwitch { get; set; }
    public CodecServiceStatus? AfterClose { get; set; }
    public Exception? SwitchError { get; set; }
    public List<string> Calls { get; } = [];
    public List<(CodecStartRequest Request, string? Password, bool Resume)> Switches { get; } = [];
    public int Restores { get; private set; }
    public int NoWaitCloses { get; private set; }
    public string? RestorePassword { get; private set; }
    public void RequestCloseNoWait() => NoWaitCloses++;
    public Task RestoreLeftoversAsync(FramePreferences preferences, string? password, CancellationToken cancellationToken)
    {
        Calls.Add("restore"); Restores++; RestorePassword = password;
        return Task.CompletedTask;
    }
    public Task<CodecServiceStatus> ReadStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);
    public Task<CodecServiceStatus> StartAsync(CodecStartRequest request, FramePreferences preferences, string? password,
        IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken, bool resume)
    {
        Calls.Add("start");
        Switches.Add((request, password, resume));
        if (SwitchError is not null) throw SwitchError;
        if (AfterSwitch is not null) Status = AfterSwitch;
        return Task.FromResult(Status);
    }
    public Task<CodecServiceStatus> CloseAsync(IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken)
    {
        Calls.Add("close");
        Status = AfterClose ?? CodecServiceStatus.NotStarted;
        return Task.FromResult(Status);
    }
    public List<int> Rates { get; } = [];
    public List<(string RateControl, int Preset, int Vbv)> Encoders { get; } = [];
    public Exception? EncoderError { get; set; }
    public List<string> Modes { get; } = [];
    public Task<CodecServiceStatus> SetModeAsync(string mode, IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken)
    {
        Calls.Add("mode");
        Modes.Add(mode);
        return Task.FromResult(Status);
    }
    public Task<CodecServiceStatus> SetEncoderAsync(string rateControl, int preset, int vbvFrames, IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken)
    {
        Calls.Add("encoder");
        if (EncoderError is not null) throw EncoderError;
        Encoders.Add((rateControl, preset, vbvFrames));
        return Task.FromResult(Status);
    }
    public Exception? RateError { get; set; }
    public Task<CodecServiceStatus> SetRateAsync(int targetMbps, IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken)
    {
        Calls.Add("rate");
        if (RateError is not null) throw RateError;
        Rates.Add(targetMbps);
        Status = Status with { TargetMbps = targetMbps };
        return Task.FromResult(Status);
    }
}

internal sealed class FakeLive : ILiveBitrate
{
    public bool SteamVrRunning { get; set; } = true;
    public Exception? Error { get; set; }
    public List<int> Applied { get; } = [];
    public List<int> FileWrites { get; } = [];
    public double? Target { get; set; }
    public Task ApplyAsync(int targetMbps)
    {
        if (Error is not null) throw Error;
        Applied.Add(targetMbps);
        Target = targetMbps;
        return Task.CompletedTask;
    }
    public Task<double?> ReadTargetAsync() => Task.FromResult(Target);
    public Task WriteFileAsync(int targetMbps) { FileWrites.Add(targetMbps); Target = targetMbps; return Task.CompletedTask; }
}

internal sealed class FakeVault : ICredentialVault
{
    public string? Stored { get; set; }
    public int Saves { get; private set; }
    public string? Load() => Stored;
    public void Save(string password) { Stored = password; Saves++; }
    public void Delete() => Stored = null;
}

internal sealed class FakePatch : IDriverPatch
{
    public BitratePatchState State { get; set; } = BitratePatchState.Patched;
    public bool SteamVrRunning { get; set; }
    public List<string> Calls { get; } = [];
    private BitratePatchStatus Make() => new("driver.dll", "driver.bak", State, BitrateBackupState.VerifiedOriginal, "x", "x", SteamVrRunning, "msg");
    public Task<BitratePatchStatus> InspectAsync() { Calls.Add("inspect"); return Task.FromResult(Make()); }
    public Task<BitratePatchStatus> ApplyAsync() { Calls.Add("apply"); State = BitratePatchState.Patched; return Task.FromResult(Make()); }
    public Task<BitratePatchStatus> RestoreAsync() { Calls.Add("restore"); State = BitratePatchState.Original; return Task.FromResult(Make()); }
}

public sealed class TrayControllerTests
{
    private static CodecServiceStatus Live(string state = "ready", string codec = "H.264", bool keep = true, int target = 300) =>
        new() { Live = true, State = state, ActiveCodec = codec, Keep = keep, TargetMbps = target, Message = state };

    private sealed class Rig
    {
        public FakeCodec Codec { get; } = new();
        public FakeVault Vault { get; } = new();
        public FakePatch Patch { get; } = new();
        public FramePreferences Prefs { get; } = new() { CodecTargetMbps = 300 };
        public FakeLive Live { get; } = new();
        public int Saves { get; private set; }
        public int RestartPrompts { get; private set; }
        public bool AllowRestart { get; set; } = true;
        public TrayController Controller { get; }
        public Rig() => Controller = new(Codec, Vault, Patch, Prefs, () => { Saves++; return Task.CompletedTask; }, Live);
        public Task<ChangeOutcome> Change(int mbps, string? typed = null, bool allowPatch = true) =>
            Controller.ChangeTargetAsync(mbps, () => Task.FromResult(typed), _ => Task.FromResult(allowPatch), () => { RestartPrompts++; return Task.FromResult(AllowRestart); });
        public Task<bool> Arm(string? typed = null, bool allowPatch = true, bool allowRestart = true) =>
            Controller.ArmAsync(() => Task.FromResult(typed), _ => Task.FromResult(allowPatch), () => { RestartPrompts++; return Task.FromResult(allowRestart); });
        public Task Disarm(string? typed = null) => Controller.DisarmAsync(() => Task.FromResult(typed));
    }

    [Fact]
    public async Task Arming_with_a_stored_password_starts_a_kept_H264_session_that_restarts_a_running_SteamVR_once()
    {
        var rig = new Rig { };
        rig.Vault.Stored = "pw"; rig.Codec.AfterSwitch = Live();
        Assert.True(await rig.Arm());
        var call = Assert.Single(rig.Codec.Switches);
        Assert.Equal(300, call.Request.TargetMbps); Assert.Equal("vbr", call.Request.RateControl); Assert.Equal(2, call.Request.Preset);
        Assert.Equal("pw", call.Password); Assert.False(call.Resume); Assert.Equal(1, rig.RestartPrompts);
        Assert.True(rig.Prefs.KeepH264); Assert.Equal(TrayState.Armed, rig.Controller.Current.State);
        Assert.Equal(0, rig.Vault.Saves);
    }

    [Fact]
    public async Task Arming_with_SteamVR_closed_waits_for_it_without_asking_or_launching_it()
    {
        var rig = new Rig();
        rig.Live.SteamVrRunning = false; rig.Vault.Stored = "pw"; rig.Codec.AfterSwitch = Live("waiting");
        Assert.True(await rig.Arm());
        var call = Assert.Single(rig.Codec.Switches);
        Assert.True(call.Resume); Assert.Equal(0, rig.RestartPrompts); Assert.Equal(TrayState.Waiting, rig.Controller.Current.State);
    }

    [Fact]
    public async Task Declining_the_SteamVR_restart_arms_nothing()
    {
        var rig = new Rig();
        rig.Vault.Stored = "pw";
        Assert.False(await rig.Arm(allowRestart: false));
        Assert.Empty(rig.Codec.Switches); Assert.False(rig.Prefs.KeepH264);
    }

    [Fact]
    public async Task The_experiment_options_travel_with_the_start_request()
    {
        var rig = new Rig();
        rig.Vault.Stored = "pw"; rig.Prefs.HookRateControl = "cbr"; rig.Prefs.HookPreset = 4; rig.Codec.AfterSwitch = Live();
        await rig.Arm();
        var request = Assert.Single(rig.Codec.Switches).Request;
        Assert.Equal("cbr", request.RateControl); Assert.Equal(4, request.Preset);
    }

    [Fact]
    public async Task A_typed_password_is_stored_only_after_the_Frame_accepted_it()
    {
        var rig = new Rig();
        rig.Codec.AfterSwitch = Live();
        Assert.True(await rig.Arm("typed"));
        Assert.Equal("typed", rig.Vault.Stored); Assert.Equal(1, rig.Vault.Saves);

        var failing = new Rig();
        failing.Codec.SwitchError = new InvalidOperationException("wrong password");
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Arm("bad"));
        Assert.Null(failing.Vault.Stored); Assert.False(failing.Prefs.KeepH264);
        Assert.Equal(TrayState.Error, failing.Controller.Current.State);
    }

    [Fact]
    public async Task Cancelling_the_password_prompt_arms_nothing()
    {
        var rig = new Rig();
        Assert.False(await rig.Arm(null));
        Assert.Empty(rig.Codec.Switches); Assert.False(rig.Prefs.KeepH264); Assert.Equal(TrayState.Disarmed, rig.Controller.Current.State);
    }

    [Fact]
    public async Task Above_232_Mbps_the_bitrate_patch_is_required_first()
    {
        var rig = new Rig();
        rig.Vault.Stored = "pw"; rig.Prefs.CodecTargetMbps = 450; rig.Patch.State = BitratePatchState.Original; rig.Codec.AfterSwitch = Live(target: 450);
        Assert.True(await rig.Arm());
        Assert.Equal(["inspect", "apply"], rig.Patch.Calls);

        var declined = new Rig();
        declined.Vault.Stored = "pw"; declined.Prefs.CodecTargetMbps = 450; declined.Patch.State = BitratePatchState.Original;
        await Assert.ThrowsAsync<InvalidOperationException>(() => declined.Arm(allowPatch: false));
        Assert.Empty(declined.Codec.Switches);

        var running = new Rig();
        running.Vault.Stored = "pw"; running.Prefs.CodecTargetMbps = 450; running.Patch.State = BitratePatchState.Original; running.Patch.SteamVrRunning = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => running.Arm());
        Assert.Contains("Close SteamVR", error.Message); Assert.DoesNotContain("apply", running.Patch.Calls); Assert.Empty(running.Codec.Switches);
    }

    [Fact]
    public async Task Bitrates_up_to_232_never_touch_the_driver()
    {
        var rig = new Rig();
        rig.Vault.Stored = "pw"; rig.Prefs.CodecTargetMbps = 232; rig.Codec.AfterSwitch = Live(target: 232);
        await rig.Arm();
        Assert.Empty(rig.Patch.Calls);
    }

    [Fact]
    public async Task Arming_again_on_a_live_session_with_another_bitrate_ends_it_and_starts_a_fresh_one_and_is_idempotent_when_unchanged()
    {
        var rig = new Rig();
        rig.Codec.Status = Live(target: 300);
        Assert.True(await rig.Arm());
        Assert.Empty(rig.Codec.Switches);

        rig.Vault.Stored = "pw"; rig.Prefs.CodecTargetMbps = 400; rig.Codec.AfterSwitch = Live(target: 400);
        Assert.True(await rig.Arm());
        var call = Assert.Single(rig.Codec.Switches);
        Assert.Equal(400, call.Request.TargetMbps);
    }

    [Fact]
    public async Task A_live_session_that_does_not_survive_SteamVR_restarts_is_closed_first()
    {
        var rig = new Rig();
        rig.Vault.Stored = "pw"; rig.Codec.Status = Live(keep: false, codec: "HEVC"); rig.Codec.AfterSwitch = Live();
        await rig.Arm();
        Assert.Equal(["close", "start"], rig.Codec.Calls);
    }

    [Fact]
    public async Task Disarming_closes_the_live_session_and_forgets_the_armed_wish()
    {
        var rig = new Rig();
        rig.Prefs.KeepH264 = true; rig.Codec.Status = Live();
        await rig.Disarm();
        Assert.Equal(["close"], rig.Codec.Calls); Assert.False(rig.Prefs.KeepH264); Assert.Equal(TrayState.Disarmed, rig.Controller.Current.State);
    }

    [Fact]
    public async Task Disarming_after_a_crash_restores_the_leftover_settings_through_a_restore_session()
    {
        var rig = new Rig();
        Directory.CreateDirectory(rig.Codec.SessionDirectory);
        await File.WriteAllTextAsync(Path.Combine(rig.Codec.SessionDirectory, "host-original.json"), """{"original":{"targetBandwidth":100},"applied":{"targetBandwidth":400.0}}""");
        rig.Vault.Stored = "pw"; rig.Prefs.KeepH264 = true;
        await rig.Disarm();
        Assert.Equal(1, rig.Codec.Restores); Assert.Equal("pw", rig.Codec.RestorePassword); Assert.Empty(rig.Codec.Switches);
        Assert.Equal(["restore"], rig.Codec.Calls); Assert.False(rig.Prefs.KeepH264);
    }

    [Fact]
    public async Task Disarming_with_nothing_applied_touches_no_session()
    {
        var rig = new Rig();
        rig.Prefs.KeepH264 = true;
        await rig.Disarm();
        Assert.Empty(rig.Codec.Calls); Assert.False(rig.Prefs.KeepH264);
    }

    [Fact]
    public async Task Launch_attaches_to_a_live_kept_session_without_switching()
    {
        var rig = new Rig();
        rig.Codec.Status = Live("waiting");
        await rig.Controller.StartAsync();
        Assert.Empty(rig.Codec.Calls); Assert.Equal(TrayState.Waiting, rig.Controller.Current.State);
    }

    [Fact]
    public async Task Launch_resumes_an_armed_session_using_the_stored_password()
    {
        var rig = new Rig();
        rig.Prefs.KeepH264 = true; rig.Vault.Stored = "pw"; rig.Codec.Status = CodecServiceStatus.NotStarted; rig.Codec.AfterSwitch = Live("waiting");
        await rig.Controller.StartAsync();
        var call = Assert.Single(rig.Codec.Switches);
        Assert.True(call.Resume); Assert.Equal("pw", call.Password); Assert.Equal(TrayState.Waiting, rig.Controller.Current.State);
    }

    [Fact]
    public async Task Launch_does_nothing_when_it_was_not_armed_and_drops_the_wish_when_the_password_is_gone()
    {
        var idle = new Rig();
        await idle.Controller.StartAsync();
        Assert.Empty(idle.Codec.Calls); Assert.Equal(TrayState.Disarmed, idle.Controller.Current.State);

        var lost = new Rig();
        lost.Prefs.KeepH264 = true;
        await lost.Controller.StartAsync();
        Assert.Empty(lost.Codec.Calls); Assert.False(lost.Prefs.KeepH264); Assert.Equal(TrayState.Disarmed, lost.Controller.Current.State);
    }

    [Fact]
    public async Task A_supervisor_that_ends_on_its_own_returns_the_tray_to_disarmed()
    {
        var rig = new Rig();
        rig.Codec.AfterSwitch = Live(); rig.Vault.Stored = "pw";
        await rig.Arm();
        rig.Codec.Status = CodecServiceStatus.NotStarted with { Message = "SteamVR restored" };
        await rig.Controller.RefreshAsync();
        Assert.Equal(TrayState.Disarmed, rig.Controller.Current.State); Assert.False(rig.Prefs.KeepH264);
        Assert.Contains("released", rig.Controller.Current.Message);
    }

    [Fact]
    public async Task Status_maps_to_tray_states()
    {
        var rig = new Rig();
        foreach (var (state, expected) in new[] { ("starting", TrayState.Arming), ("switching", TrayState.Arming), ("ready", TrayState.Armed), ("waiting", TrayState.Waiting), ("error", TrayState.Error) })
        {
            rig.Codec.Status = Live(state);
            await rig.Controller.RefreshAsync();
            Assert.Equal(expected, rig.Controller.Current.State);
        }
        rig.Codec.Status = Live("ready", codec: "HEVC", keep: false);
        await rig.Controller.RefreshAsync();
        Assert.Equal(TrayState.Disarmed, rig.Controller.Current.State);
    }

    private static CodecServiceStatus LiveRate(string state = "ready", int target = 300) => Live(state, target: target) with { RateLive = true };

    [Fact]
    public async Task Changing_the_bitrate_of_a_running_session_is_applied_live_without_a_restart()
    {
        var rig = new Rig();
        rig.Prefs.KeepH264 = true; rig.Codec.Status = LiveRate();
        await rig.Controller.RefreshAsync();
        Assert.Equal(ChangeOutcome.AppliedLive, await rig.Change(400));
        Assert.Equal([400], rig.Codec.Rates); Assert.Equal([400], rig.Live.Applied);
        Assert.Equal(["rate"], rig.Codec.Calls); Assert.Equal(0, rig.RestartPrompts);
        Assert.Equal(400, rig.Prefs.CodecTargetMbps); Assert.Equal(TrayState.Armed, rig.Controller.Current.State); Assert.Equal(400, rig.Controller.Current.TargetMbps);
    }

    [Fact]
    public async Task Encoder_options_are_sent_to_an_armed_H264_session_without_touching_SteamVR()
    {
        var rig = new Rig();
        rig.Prefs.KeepH264 = true; rig.Prefs.HookRateControl = "cbr"; rig.Prefs.HookPreset = 4; rig.Prefs.HookVbvFrames = 2;
        rig.Codec.Status = LiveRate(target: 300);
        await rig.Controller.RefreshAsync();
        Assert.True(await rig.Controller.ApplyEncoderOptionsAsync());
        Assert.Equal([("cbr", 4, 2)], rig.Codec.Encoders); Assert.Empty(rig.Live.Applied);
    }

    [Fact]
    public async Task A_live_codec_switch_goes_to_the_supervisor_only_for_a_hooked_ready_session()
    {
        var rig = new Rig();
        rig.Prefs.KeepH264 = true; rig.Codec.Status = LiveRate() with { ModeLive = true };
        await rig.Controller.RefreshAsync();
        Assert.True(await rig.Controller.SwitchCodecLiveAsync(stock: true));
        Assert.True(await rig.Controller.SwitchCodecLiveAsync(stock: false));
        Assert.Equal(["stock", "h264"], rig.Codec.Modes); Assert.Empty(rig.Live.Applied);

        var unhooked = new Rig();
        unhooked.Prefs.KeepH264 = true; unhooked.Codec.Status = LiveRate();
        await unhooked.Controller.RefreshAsync();
        Assert.False(await unhooked.Controller.SwitchCodecLiveAsync(stock: true));
        Assert.Empty(unhooked.Codec.Modes);
    }

    [Fact]
    public async Task Streaming_stock_HEVC_in_an_armed_session_is_described_as_such()
    {
        var rig = new Rig();
        rig.Prefs.KeepH264 = true; rig.Codec.Status = LiveRate() with { ActiveCodec = "HEVC", ModeLive = true };
        await rig.Controller.RefreshAsync();
        Assert.Equal(TrayState.Armed, rig.Controller.Current.State); Assert.Contains("HEVC", rig.Controller.Current.Message);
    }

    [Fact]
    public async Task Encoder_options_wait_for_the_next_arm_when_there_is_no_live_session()
    {
        var disarmed = new Rig();
        Assert.False(await disarmed.Controller.ApplyEncoderOptionsAsync());
        var waiting = new Rig();
        waiting.Prefs.KeepH264 = true; waiting.Codec.Status = LiveRate("waiting");
        await waiting.Controller.RefreshAsync();
        Assert.False(await waiting.Controller.ApplyEncoderOptionsAsync());
        Assert.Empty(disarmed.Codec.Encoders); Assert.Empty(waiting.Codec.Encoders); Assert.Empty(waiting.Live.Applied);
    }

    [Fact]
    public async Task While_SteamVR_is_closed_the_supervisor_records_the_bitrate_and_SteamVR_is_not_touched()
    {
        var rig = new Rig();
        rig.Prefs.KeepH264 = true; rig.Live.SteamVrRunning = false; rig.Codec.Status = LiveRate("waiting");
        await rig.Controller.RefreshAsync();
        Assert.Equal(ChangeOutcome.AppliedLive, await rig.Change(350));
        Assert.Equal([350], rig.Codec.Rates); Assert.Empty(rig.Live.Applied);
    }

    [Fact]
    public async Task A_session_that_cannot_change_live_is_re_applied_only_after_the_user_agrees_to_the_restart()
    {
        var rig = new Rig();
        rig.Vault.Stored = "pw"; rig.Prefs.KeepH264 = true; rig.Codec.Status = Live(target: 300); rig.Codec.AfterSwitch = Live(target: 400);
        await rig.Controller.RefreshAsync();
        Assert.Equal(ChangeOutcome.Reapplied, await rig.Change(400));
        Assert.Equal(1, rig.RestartPrompts); Assert.Empty(rig.Codec.Rates);
        var call = Assert.Single(rig.Codec.Switches); Assert.Equal(400, call.Request.TargetMbps);

        var declined = new Rig { AllowRestart = false };
        declined.Prefs.KeepH264 = true; declined.Codec.Status = Live(target: 300);
        await declined.Controller.RefreshAsync();
        Assert.Equal(ChangeOutcome.Declined, await declined.Change(400));
        Assert.Empty(declined.Codec.Switches); Assert.Empty(declined.Codec.Rates); Assert.Equal(400, declined.Prefs.CodecTargetMbps);
    }

    [Fact]
    public async Task Automatic_bandwidth_is_reported_but_never_changed_by_the_tray()
    {
        var rig = new Rig();
        rig.Codec.Status = Live() with { AutomaticBandwidth = true };
        await rig.Controller.RefreshAsync();
        Assert.Contains("Automatic bandwidth", rig.Controller.Current.Message);
        Assert.Empty(rig.Live.FileWrites); Assert.Empty(rig.Live.Applied);
    }

    [Fact]
    public void Session_end_asks_the_supervisor_to_close_without_waiting_and_keeps_the_armed_wish()
    {
        var rig = new Rig();
        rig.Prefs.KeepH264 = true;
        rig.Controller.RequestCloseNoWait();
        Assert.Equal(1, rig.Codec.NoWaitCloses); Assert.True(rig.Prefs.KeepH264);
    }

    [Fact]
    public async Task Changing_the_bitrate_while_disarmed_goes_to_SteamVR_without_touching_the_codec_service()
    {
        var rig = new Rig();
        Assert.Equal(ChangeOutcome.AppliedLive, await rig.Change(500));
        Assert.Empty(rig.Codec.Calls); Assert.Equal(500, rig.Prefs.CodecTargetMbps); Assert.Equal([500], rig.Live.Applied);
    }

    [Fact]
    public async Task A_live_change_above_232_still_needs_the_bitrate_patch()
    {
        var rig = new Rig();
        rig.Prefs.KeepH264 = true; rig.Codec.Status = LiveRate(); rig.Patch.State = BitratePatchState.Original;
        await rig.Controller.RefreshAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Change(450, allowPatch: false));
        Assert.Empty(rig.Codec.Rates); Assert.Empty(rig.Live.Applied);
        Assert.Equal(["inspect"], rig.Patch.Calls);
    }

    [Fact]
    public async Task A_refused_live_change_leaves_the_session_armed_and_reports_the_error()
    {
        var rig = new Rig();
        rig.Prefs.KeepH264 = true; rig.Codec.Status = LiveRate(); rig.Codec.RateError = new InvalidOperationException("Live bitrate change unavailable");
        await rig.Controller.RefreshAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Change(400));
        Assert.Equal(TrayState.Armed, rig.Controller.Current.State); Assert.True(rig.Prefs.KeepH264); Assert.Empty(rig.Live.Applied);

        var liveFails = new Rig();
        liveFails.Prefs.KeepH264 = true; liveFails.Codec.Status = LiveRate(); liveFails.Live.Error = new IOException("settings error 5");
        await liveFails.Controller.RefreshAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => liveFails.Change(400));
        Assert.Contains("saved", error.Message); Assert.Equal([400], liveFails.Codec.Rates);
    }

    [Fact]
    public async Task Bitrate_choices_are_validated_and_saved()
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Controller.SetTargetAsync(501));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Controller.SetTargetAsync(24));
        await rig.Controller.SetTargetAsync(500);
        Assert.Equal(500, rig.Prefs.CodecTargetMbps); Assert.Equal(1, rig.Saves);
        Assert.Equal(500, rig.Controller.Current.TargetMbps);
    }
}
