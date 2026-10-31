using SteamFrame.Core;

namespace SteamFrameTray;

/// <summary>The in-VR HUD: a panel on a controller with live readouts and laser-pointer controls for the codec and encoder settings.</summary>
public sealed partial class TrayApp
{
    private HudSettings _hudSettings = new();
    private HudController? _hud;
    private readonly System.Windows.Forms.Timer _hudTimer = new() { Interval = 33 };
    private StatsSample? _lastSample;

    private void StartHud()
    {
        _hudSettings = HudSettings.Load(TrayData.HudPath);
        _hud = new HudController(new OpenVrOverlayApi(_paths.SteamVrRoot, HudLog), _hudSettings, ReadHudView, new HudActions(this), SaveHud,
            () => _lastSample?.SteamVrRunning ?? false, HudLog);
        _statsService.Sampled += (sample, _) => _lastSample = sample;
        _statsService.HudActive = _hudSettings.Enabled;
        _hudTimer.Tick += (_, _) =>
        {
            try { _hud.Tick(); }
            catch (Exception error) when (error is not OutOfMemoryException) { HudLog("HUD tick failed: " + error.Message); _hud.Teardown(); }
        };
        _hudTimer.Start();
    }

    private void StopHud()
    {
        _hudTimer.Stop();
        _hud?.Dispose();
    }

    private HudView ReadHudView()
    {
        var sample = _lastSample;
        var tray = _controller.Current;
        var status = tray.State is TrayState.Error or TrayState.Waiting ? tray.Message : "";
        return new(sample?.SteamVrRunning ?? false, sample?.Codec ?? "—", sample?.CodecVerified ?? false, sample?.Mbps, sample?.Fps, tray.TargetMbps, tray.IsArmed,
            tray.State is TrayState.Arming or TrayState.Disarming, status, _preferences.HookRateControl, _preferences.HookPreset, _preferences.HookVbvFrames,
            sample?.Health?.CompositorLate ?? 0, sample?.Health?.CompositorDropped ?? 0, false, "left");
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
        void Save() { try { SaveHud(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Balloon("In-VR HUD", error.Message, ToolTipIcon.Warning); } }
        show.Click += (_, _) =>
        {
            _hudSettings.Enabled = !_hudSettings.Enabled;
            _statsService.HudActive = _hudSettings.Enabled;
            Save();
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
