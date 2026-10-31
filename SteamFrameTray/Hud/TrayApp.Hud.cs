using SteamFrame.Core;

namespace SteamFrameTray;

/// <summary>The in-VR HUD: a panel on a controller with live readouts and laser-pointer controls for the codec and encoder settings. It runs in a helper process.</summary>
public sealed partial class TrayApp
{
    private HudSettings _hudSettings = new();
    private HudHostClient? _hud;
    private readonly System.Windows.Forms.Timer _hudTimer = new() { Interval = 250 };
    private StatsSample? _lastSample;
    private HudView? _sentView;

    private void StartHud()
    {
        _hudSettings = HudSettings.Load(TrayData.HudPath);
        _hud = new HudHostClient(OnHudMessage, HudLog);
        _statsService.Sampled += (sample, _) => _lastSample = sample;
        _hudTimer.Tick += (_, _) => PushHudView();
        _hudTimer.Start();
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
                SaveHud();
                break;
            case "action":
                HudLog($"HUD button: {message.Kind} h264={message.H264} mbps={message.Mbps} {message.RateControl} P{message.Preset} V{message.VbvFrames}");
                var actions = new HudActions(this);
                _ = message.Kind switch
                {
                    "codec" => actions.SetCodecAsync(message.H264),
                    "bitrate" => actions.SetBitrateAsync(message.Mbps),
                    "encoder" when message.RateControl is not null => actions.SetEncoderAsync(message.RateControl, message.Preset, message.VbvFrames),
                    _ => Task.CompletedTask
                };
                break;
        }
    }

    private HudView ReadHudView()
    {
        var sample = _lastSample;
        var tray = _controller.Current;
        var status = tray.State is TrayState.Error or TrayState.Waiting ? tray.Message : "";
        return new(sample?.SteamVrRunning ?? false, sample?.Codec ?? "—", sample?.CodecVerified ?? false,
            sample?.Mbps is { } mbps ? Math.Round(mbps) : null, sample?.Fps is { } fps ? Math.Round(fps) : null, tray.TargetMbps, tray.IsArmed,
            tray.State is TrayState.Arming or TrayState.Disarming, status, _preferences.HookRateControl, _preferences.HookPreset, _preferences.HookVbvFrames,
            sample?.Health?.CompositorLate ?? 0, sample?.Health?.CompositorDropped ?? 0, false, "left", HudView.Pack(_statsService.Sampler.History));
    }

    private void SaveHud() => _hudSettings.Save(TrayData.HudPath);

    private static void HudLog(string line)
    {
        try
        {
            var path = TrayData.HudLogPath;
            Directory.CreateDirectory(TrayData.Root);
            if (File.Exists(path) && new FileInfo(path).Length > 200_000) File.Delete(path);
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine);
        }
        catch (IOException) { /* logging must never break the tray */ }
    }

    private ToolStripMenuItem BuildHudMenu()
    {
        var root = new ToolStripMenuItem("In-VR HUD");
        var show = new ToolStripMenuItem("Show on a controller");
        var left = new ToolStripMenuItem("Attach to the left controller");
        var right = new ToolStripMenuItem("Attach to the right controller");
        var reset = new ToolStripMenuItem("Reset HUD position");
        void Save() { try { SaveHud(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Balloon("In-VR HUD", error.Message, ToolTipIcon.Warning); } PushHudSettings(); }
        show.Click += (_, _) =>
        {
            _hudSettings.Enabled = !_hudSettings.Enabled;
            Save();
            ApplyHudEnabled();
            if (_hudSettings.Enabled) Balloon("In-VR HUD", "The HUD appears on the " + (_hudSettings.IsLeft ? "left" : "right") + " controller once SteamVR is running. Use its Move button to place it.", ToolTipIcon.Info);
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

        public Task SetBitrateAsync(int mbps) => app.RunAsync(() => app.ChangeTargetAsync(mbps));

        public Task SetEncoderAsync(string rateControl, int preset, int vbvFrames) => app.RunAsync(async () =>
        {
            app._preferences.HookRateControl = rateControl;
            app._preferences.HookPreset = preset;
            app._preferences.HookVbvFrames = vbvFrames;
            await app._preferences.SaveAsync(TrayData.SettingsPath);
            try { await app._controller.ApplyEncoderOptionsAsync(); }
            catch (Exception error) when (error is not OperationCanceledException) { HudLog("Encoder options not applied live: " + error.Message); }
        });
    }
}
