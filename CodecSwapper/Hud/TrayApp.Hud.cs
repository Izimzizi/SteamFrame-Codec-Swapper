using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>The in-VR HUD: a panel on a controller with live readouts and laser-pointer controls for the codec and encoder settings. It runs in a helper process.</summary>
public sealed partial class TrayApp
{
    private HudSettings _hudSettings = new();
    private HudHostClient? _hud;
    private readonly System.Windows.Forms.Timer _hudTimer = new() { Interval = 100 };
    private StatsSample? _lastSample;
    private HudView? _sentView;
    private string _hudNotice = "";
    private bool _hudNoticeIsError;
    private DateTime _hudNoticeUntil;
    private bool _steamVrSeen;
    private long _steamVrCheckedAt;
    private int _hudWidth;                   // the stream width the HUD shows (0 until read)
    private long _hudWidthReadAt;

    private void StartHud()
    {
        _hudSettings = HudSettings.Load(AppData.HudPath);
        _hud = new HudHostClient(OnHudMessage, HudLog) { Started = OnHudHelperStarted };
        WriteHevcOptions();
        _statsService.Sampled += (sample, _) => _lastSample = sample;
        _steamVrSeen = OpenVrOverlayApi.IsVrServerRunning();
        _hudTimer.Tick += (_, _) => { FollowSteamVrStart(); RefreshHudWidth(); PushHudView(); };
        _hudTimer.Start();
        ApplyHudEnabled();
    }

    /// <summary>Hide HUD lasts until SteamVR starts again; each start the HUD shows or stays hidden as the "Show when SteamVR starts" setting says. Polled here because a hidden HUD has no sampling to notice it.</summary>
    private void FollowSteamVrStart()
    {
        if (Environment.TickCount64 - _steamVrCheckedAt < 1000) return;
        _steamVrCheckedAt = Environment.TickCount64;
        var running = OpenVrOverlayApi.IsVrServerRunning();
        var started = running && !_steamVrSeen;
        _steamVrSeen = running;
        if (!started || _hudSettings.Enabled == _hudSettings.AutoShow) return;
        _hudSettings.Enabled = _hudSettings.AutoShow;
        ApplyHudEnabled();
    }

    private void StopHud()
    {
        _hudTimer.Stop();
        _hud?.Dispose();
    }

    private void ApplyHudEnabled()
    {
        _statsService.HudActive = _hudSettings.Enabled;
        if (_hud is null) return;
        if (_hudSettings.Enabled)
        {
            _hud.Start();
            PushHudSettings();
            _sentView = null;
        }
        else _hud.Stop();
    }

    /// <summary>A helper that has just (re)started knows nothing: it exits when SteamVR does and is restarted, so it is given the settings and the current view again.</summary>
    private void OnHudHelperStarted() { PushHudSettings(); _sentView = null; }

    private void PushHudSettings() => _hud?.Send(new() { Type = "settings", Settings = _hudSettings });

    private void PushHudView()
    {
        if (_hud is not { Running: true }) return;
        var view = ReadHudView();
        if (view == _sentView) return;
        _sentView = view;
        _hud.Send(new() { Type = "view", View = view });
    }

    private void OnHudMessage(HudMessage message)
    {
        switch (message.Type)
        {
            case "log": HudLog(message.Line ?? ""); break;
            case "settings" when message.Settings is not null:
                _hudSettings.Placement = message.Settings.Placement;
                _hudSettings.WidthMeters = message.Settings.WidthMeters;
                _hudSettings.IconPlacement = message.Settings.IconPlacement;
                _hudSettings.IconWidthMeters = message.Settings.IconWidthMeters;
                _hudSettings.AutoShow = message.Settings.AutoShow;
                _hudSettings.ExpandOnHover = message.Settings.ExpandOnHover;
                _hudSettings.Watch = message.Settings.Watch;
                _hudSettings.OwnLaser = message.Settings.OwnLaser;
                _hudSettings.FadeAngle = message.Settings.FadeAngle;
                _hudSettings.FadeDistanceCm = message.Settings.FadeDistanceCm;
                _hudSettings.GuideDone = message.Settings.GuideDone;
                SaveHud();
                break;
            case "action":
                HudLog($"HUD button: {message.Kind} h264={message.H264} mbps={message.Mbps} {message.RateControl} P{message.Preset} V{message.VbvFrames}");
                var actions = new HudActions(this);
                _ = message.Kind switch
                {
                    "codec" => actions.SetCodecAsync(message.H264),
                    "bitrate" => actions.SetBitrateAsync(message.Mbps),
                    "width" => actions.SetWidthAsync(message.Width),
                    "hide" => actions.HideAsync(),
                    "counters" => actions.ResetCountersAsync(),
                    "multipass" => actions.SetMultiPassAsync(message.MultiPass),
                    "encoder" when message.RateControl is not null => actions.SetEncoderAsync(message.RateControl, message.Preset, message.VbvFrames),
                    _ => Task.CompletedTask
                };
                break;
        }
    }

    /// <summary>Warnings and errors the tray tells the user in a desktop balloon are also written on the HUD for a while, since the headset hides the desktop. Confirmations are not.</summary>
    private void ShowOnHud(string title, string text, ToolTipIcon icon)
    {
        if (!_hudSettings.Enabled || icon is not (ToolTipIcon.Warning or ToolTipIcon.Error)) return;
        _hudNotice = (title.Length > 0 ? title + ": " : "") + text;
        _hudNoticeIsError = icon == ToolTipIcon.Error;
        _hudNoticeUntil = DateTime.UtcNow.AddSeconds(icon is ToolTipIcon.Warning or ToolTipIcon.Error ? 14 : 7);
    }

    private HudView ReadHudView()
    {
        var sample = _lastSample;
        var tray = _controller.Current;
        var status = tray.State is TrayState.Error or TrayState.Waiting ? tray.Message : "";
        return new(sample?.SteamVrRunning ?? false, sample?.Codec ?? "—", sample?.CodecVerified ?? false,
            sample?.Mbps is { } mbps ? Math.Round(mbps) : null, sample?.Fps is { } fps ? Math.Round(fps, 1) : null, tray.TargetMbps, tray.IsArmed,
            tray.State is TrayState.Arming or TrayState.Disarming, status, _preferences.HookRateControl, _preferences.HookPreset, _preferences.HookVbvFrames,
            false, "left", HudView.Pack(_statsService.Sampler.History), HudAverage(), HudEncoder(sample), HudHealth(sample), "",
            Notice: DateTime.UtcNow < _hudNoticeUntil ? _hudNotice : "", NoticeIsError: _hudNoticeIsError, Latency: sample?.Timing?.Describe() ?? "", WidthPx: _hudWidth, MultiPass: _preferences.HookMultiPass);
    }

    /// <summary>The stream width is read from the settings file (or SteamVR) every couple of seconds while the HUD runs.</summary>
    private void RefreshHudWidth()
    {
        if (_hud is not { Running: true } || Environment.TickCount64 - _hudWidthReadAt < 2000) return;
        _hudWidthReadAt = Environment.TickCount64;
        _ = ReadHudWidthAsync();
    }

    private async Task ReadHudWidthAsync()
    {
        try { if (await StreamWidth.ReadAsync(_paths) is int width && width > 0) _hudWidth = width; }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException) { /* shown again at the next read */ }
    }

    // The same readouts as the monitor window, as one line each.
    private string HudAverage() => _statsService.Sampler.RecentAverage is double average ? $"avg 60 s {average:0} Mbps" : "";

    private static string HudEncoder(StatsSample? sample) => sample?.Config is { } c
        ? $"encoder asked {c.AverageMbps:0} avg / {c.MaxMbps:0} peak Mbps ({c.RateControl}, {c.Preset}{(c.VbvSize > 0 ? $", VBV {c.VbvSize / 1e6:0.0} Mbit" : "")}{(c.Fps is double cf ? $", {cf:0} fps" : "")})"
        : "";

    private static string HudHealth(StatsSample? sample)
    {
        if (sample is null) return "";
        var parts = new List<string>();
        if (sample.Health is { } h)
        {
            parts.Add($"held back {h.HeldTotal} ({h.HeldLastMinute} in 60 s)");
            if (h.CompositorAvailable) parts.Add($"PC {h.CompositorDropped} dropped, {h.CompositorLate} late");
        }
        if (sample.EncodeErrors > 0) parts.Add($"{sample.EncodeErrors} encode errors");
        return string.Join("  ·  ", parts);
    }

    /// <summary>Saves the encoder options and hands them to the hook, which applies them to Default (HEVC) too.</summary>
    private async Task SaveEncoderOptionsAsync()
    {
        await _preferences.SaveAsync(AppData.SettingsPath);
        WriteHevcOptions();
    }

    private void WriteHevcOptions()
    {
        try { HevcOptions.Write(AppData.HevcOptionsPath, _preferences); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { HudLog("Could not write hevc-options.txt: " + error.Message); }
    }

    private void SaveHud() => _hudSettings.Save(AppData.HudPath);

    private static void HudLog(string line)
    {
        try
        {
            var path = AppData.HudLogPath;
            Directory.CreateDirectory(AppData.Root);
            if (File.Exists(path) && new FileInfo(path).Length > 200_000) File.Delete(path);
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine);
        }
        catch (IOException) { /* logging must never break the tray */ }
    }

    private ToolStripMenuItem BuildHudMenu()
    {
        var root = new ToolStripMenuItem("VR HUD");
        var show = new ToolStripMenuItem("Show HUD on a controller");
        var left = new ToolStripMenuItem("Attach to the left controller");
        var right = new ToolStripMenuItem("Attach to the right controller");
        var reset = new ToolStripMenuItem("Reset HUD position");
        void Save() { try { SaveHud(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Balloon("VR HUD", error.Message, ToolTipIcon.Warning); } PushHudSettings(); }
        show.Click += (_, _) =>
        {
            _hudSettings.Enabled = !_hudSettings.Enabled;
            Save();
            ApplyHudEnabled();
        };
        left.Click += (_, _) => { _hudSettings.Hand = "left"; Save(); };
        right.Click += (_, _) => { _hudSettings.Hand = "right"; Save(); };
        reset.Click += (_, _) => { _hudSettings.ResetPlacement(); Save(); };
        root.DropDownOpening += (_, _) =>
        {
            show.Checked = _hudSettings.Enabled;
            left.Checked = _hudSettings.IsLeft;
            right.Checked = !_hudSettings.IsLeft;
        };
        root.DropDownItems.AddRange([show, new ToolStripSeparator(), left, right, new ToolStripSeparator(), reset]);
        return root;
    }

    /// <summary>The HUD's buttons call the same operations as the tray menu.</summary>
    private sealed class HudActions(TrayApp app) : IHudActions
    {
        public Task SetCodecAsync(bool h264) => app.RunAsync(() => (h264, app._controller.Current.IsArmed) switch
        {
            (true, false) => app.ArmAsync(),
            (false, true) => app.DisarmAsync(),
            _ => Task.CompletedTask
        });

        public Task SetBitrateAsync(int mbps) => app.RunLatestAsync(() => app.ChangeTargetAsync(mbps));

        public Task SetWidthAsync(int width) => app.RunLatestAsync(async () => { await app.ChangeWidthAsync(StreamWidth.Snapped(width)); app._hudWidth = StreamWidth.Snapped(width); });

        public Task ResetCountersAsync() { app._healthReader?.ResetCounters(); return Task.CompletedTask; }

        /// <summary>The HUD's own button: it vanishes with the HUD, so the way back is told on the desktop.</summary>
        public Task HideAsync()
        {
            app._hudSettings.Enabled = false;
            app.ApplyHudEnabled();
            app._icon.BalloonTipTitle = "VR HUD hidden";
            app._icon.BalloonTipText = (app._hudSettings.AutoShow ? "It comes back when SteamVR starts again, or now from" : "Show it again from") + " the tray menu: VR HUD > Show HUD on a controller.";
            app._icon.BalloonTipIcon = ToolTipIcon.Info;
            app._icon.ShowBalloonTip(6000);
            return Task.CompletedTask;
        }

        public Task SetMultiPassAsync(int mode) => ApplyEncoderAsync(() => app._preferences.HookMultiPass = Math.Clamp(mode, 0, 2));

        public Task SetEncoderAsync(string rateControl, int preset, int vbvFrames) => ApplyEncoderAsync(() =>
        {
            app._preferences.HookRateControl = rateControl;
            app._preferences.HookPreset = preset;
            app._preferences.HookVbvFrames = vbvFrames;
        });

        // The choice is recorded at once, so quick changes to different options all count; one apply (the latest) follows the running action.
        private Task ApplyEncoderAsync(Action change)
        {
            if (app.WarnWithoutDriver()) return Task.CompletedTask;
            change();
            return app.RunLatestAsync(async () =>
        {
            await app.SaveEncoderOptionsAsync();
            try { if (!await app._controller.ApplyEncoderOptionsAsync()) HudLog("Encoder options saved; there is no live session to apply them to."); }
            catch (Exception error) when (error is not OperationCanceledException) { HudLog("Encoder options not applied live: " + error.Message); }
            });
        }
    }
}
