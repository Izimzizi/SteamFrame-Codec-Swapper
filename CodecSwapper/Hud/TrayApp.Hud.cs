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

    private void StartHud()
    {
        _hudSettings = HudSettings.Load(AppData.HudPath);
        _hud = new HudHostClient(OnHudMessage, HudLog);
        WriteHevcOptions();
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
            sample?.Mbps is { } mbps ? Math.Round(mbps) : null, sample?.Fps is { } fps ? Math.Round(fps, 1) : null, tray.TargetMbps, tray.IsArmed,
            tray.State is TrayState.Arming or TrayState.Disarming, status, _preferences.HookRateControl, _preferences.HookPreset, _preferences.HookVbvFrames,
            false, "left", HudView.Pack(_statsService.Sampler.History), HudAverage(), HudEncoder(sample), HudHealth(sample), sample is null ? "" : sample.Note ?? sample.CodecDetail ?? "");
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
            if (_hudSettings.Enabled) Balloon("VR HUD", "The HUD appears on the " + (_hudSettings.IsLeft ? "left" : "right") + " controller once SteamVR is running. Use its Move button to place it.", ToolTipIcon.Info);
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
            await app.SaveEncoderOptionsAsync();
            try { if (!await app._controller.ApplyEncoderOptionsAsync()) HudLog("Encoder options saved; there is no live session to apply them to."); }
            catch (Exception error) when (error is not OperationCanceledException) { HudLog("Encoder options not applied live: " + error.Message); }
        });
    }
}
