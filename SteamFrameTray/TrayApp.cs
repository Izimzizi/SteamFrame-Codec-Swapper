using System.Diagnostics;
using Microsoft.Win32;
using SteamFrame.Core;

namespace SteamFrameTray;

/// <summary>The notification-area icon and its menu. All state lives in <see cref="TrayController"/>.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly FramePreferences _preferences = LoadPreferences();
    private readonly SteamPaths _paths = SteamPaths.Current;
    private readonly CodecController _codec;
    private readonly TrayController _controller;
    private readonly IDriverPatch _patch;
    private readonly LiveBitrate _liveBitrate;
    private readonly EncoderProbe _probe = new();
    private readonly StatsService _statsService;
    private readonly StatsForm _stats;
    private readonly ToolStripMenuItem _record = new("Record stats log");
    private readonly NotifyIcon _icon = new() { Visible = true };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
    private readonly ToolStripMenuItem _status = new() { Enabled = false, AutoSize = false, Size = new Size(MenuBitrateSlider.MenuWidth, 24), TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripMenuItem _toggle = new();
    private readonly MenuBitrateSlider _menuSlider;
    private readonly ToolStripMenuItem _rateMenu = new("Encoder experiment (next arm)");
    private readonly ToolStripLabel _bitrateLabel = new() { AutoSize = false, Size = new Size(MenuBitrateSlider.MenuWidth, 22), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
    private readonly ToolStripMenuItem _statsItem = new("Stream stats window");
    private readonly ToolStripMenuItem _startup = new("Start with Windows");
    private readonly ToolStripMenuItem _password = new("Frame password");
    private readonly ToolStripMenuItem _setPassword = new("Set or change…");
    private readonly ToolStripMenuItem _forgetPassword = new("Forget stored password");
    private readonly ToolStripMenuItem _patchMenu = new("Bitrate patch (above 232 Mbps)");
    private readonly ToolStripMenuItem _patchState = new() { Enabled = false };
    private readonly ToolStripMenuItem _installPatch = new("Install patch");
    private readonly ToolStripMenuItem _removePatch = new("Remove patch");
    private readonly ToolStripMenuItem _admin = new("Restart as administrator");
    private readonly SetupCheck _setup;
    private SetupForm? _setupForm;
    private bool _busy, _exiting;

    public TrayApp(bool autostart)
    {
        _codec = new CodecController(sessionDirectory: TrayData.SessionDirectory, steamPaths: _paths);
        _patch = new DriverPatch(new BitratePatcher(), _paths.Driver);
        _liveBitrate = new LiveBitrate(_paths);
        _controller = new TrayController(new CodecServiceAdapter(_codec), new CredentialVault(new FrameCredentialStore()), _patch, _preferences, () => _preferences.SaveAsync(TrayData.SettingsPath), _liveBitrate);
        _setup = new SetupCheck(new ProcessRunner(), _preferences, () => _preferences.SaveAsync(TrayData.SettingsPath), new CredentialVault(new FrameCredentialStore()), SetupEnvironment.ForThisPc(_paths, Path.Combine(AppContext.BaseDirectory, "CodecLab")), _patch);
        _statsService = new StatsService(new StatsSampler(() => _probe.GetSnapshotAsync(), () => _probe.StartAsync(), health: new HealthReader(_paths, TrayData.DiagnosticsDirectory).ReadAsync, monitorAllowed: () => !_controller.Current.IsArmed), () => _controller.Current);
        _stats = new StatsForm(_statsService);
        _menuSlider = new MenuBitrateSlider(_bitrateLabel, mbps => RunAsync(() => ChangeTargetAsync(mbps)));
        BuildMenu();
        SystemEvents.SessionEnding += OnSessionEnding;
        _controller.Changed += Render;
        Render(_controller.Current);
        _icon.MouseDoubleClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left && _toggle.Enabled) await RunAsync(() => _controller.Current.IsArmed ? DisarmAsync() : ArmAsync());
        };
        _poll.Tick += async (_, _) => { try { await _controller.RefreshAsync(); } catch (Exception) { /* the next poll retries */ } };
        _poll.Start();
        _ = StartAsync(autostart);
    }

    private static FramePreferences LoadPreferences()
    {
        var preferences = FramePreferences.Load(TrayData.SettingsPath);
        // The tray keeps its pinned host key with its own files, never in the full app's folder.
        if (string.IsNullOrWhiteSpace(preferences.SshKnownHosts) || !TrayData.IsInside(preferences.SshKnownHosts)) preferences.SshKnownHosts = TrayData.KnownHosts;
        return preferences;
    }

    private async Task StartAsync(bool autostart)
    {
        try { await _controller.StartAsync(); }
        catch (Exception error) { Balloon("SteamFrame Tray", error.Message, ToolTipIcon.Error); }
        if (!autostart && !_setup.IsConfigured()) ShowSetup();
        else if (!autostart && !_controller.Current.IsArmed && _controller.Current.State != TrayState.Error)
            Balloon("SteamFrame Tray", "Right-click the icon to arm H.264.", ToolTipIcon.Info);
    }

    private void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        BuildRateMenu();
        _toggle.Click += async (_, _) => await RunAsync(() => _controller.Current.IsArmed ? DisarmAsync() : ArmAsync());
        _statsItem.Click += (_, _) => ToggleStats();
        _record.Click += (_, _) => ToggleRecording();
        _startup.Click += (_, _) => { try { StartupRegistration.Set(!StartupRegistration.IsEnabled); } catch (Exception error) { Balloon("Start with Windows", error.Message, ToolTipIcon.Error); } };
        _setPassword.Click += async (_, _) => await RunAsync(SetPasswordAsync);
        _forgetPassword.Click += async (_, _) => await RunAsync(() =>
        {
            _controller.ForgetPassword();
            Balloon("Frame password", "The stored password was removed. You will be asked for it the next time you arm.", ToolTipIcon.Info);
            return Task.CompletedTask;
        });
        _password.DropDownItems.AddRange([_setPassword, _forgetPassword]);
        _installPatch.Click += async (_, _) => await RunAsync(() => PatchAsync(install: true));
        _removePatch.Click += async (_, _) => await RunAsync(() => PatchAsync(install: false));
        _admin.Click += (_, _) => { if (Elevation.RestartElevated()) ExitApp(); };
        _patchMenu.DropDownItems.AddRange([_patchState, new ToolStripSeparator(), _installPatch, _removePatch, _admin]);
        _patchMenu.DropDownOpening += async (_, _) => await RefreshPatchMenuAsync();
        var setup = new ToolStripMenuItem("Setup / connection check…");
        setup.Click += (_, _) => ShowSetup();
        var openLogs = new ToolStripMenuItem("Open logs folder");
        openLogs.Click += (_, _) => { try { Directory.CreateDirectory(TrayData.LogsDirectory); Process.Start(new ProcessStartInfo(TrayData.LogsDirectory) { UseShellExecute = true }); } catch (Exception) { } };
        var folder = new ToolStripMenuItem("Open session folder");
        folder.Click += (_, _) => { try { Directory.CreateDirectory(_codec.SessionDirectory); Process.Start(new ProcessStartInfo(_codec.SessionDirectory) { UseShellExecute = true }); } catch (Exception) { } };
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += async (_, _) => await ExitAsync();
        menu.Items.AddRange([_status, new ToolStripSeparator(), _toggle, _bitrateLabel, _menuSlider, _rateMenu, new ToolStripSeparator(), _statsItem, _record, openLogs,
            new ToolStripSeparator(), _startup, setup, _password, _patchMenu, folder, new ToolStripSeparator(), exit]);
        menu.Opening += (_, _) =>
        {
            _startup.Checked = StartupRegistration.IsEnabled;
            _statsItem.Checked = _stats.Visible;
            _record.Checked = _statsService.Recording;
            _admin.Visible = !Elevation.IsElevated;
            _forgetPassword.Enabled = _controller.HasPassword;
        };
        _icon.ContextMenuStrip = menu;
    }

    /// <summary>Rate control and preset for the next H.264 arm. They are logged in the run header so runs can be compared.</summary>
    private void BuildRateMenu()
    {
        ToolStripMenuItem Group(string title, (string Text, Func<bool> Selected, Action Set)[] choices)
        {
            var group = new ToolStripMenuItem(title);
            var items = new List<(ToolStripMenuItem Item, Func<bool> Selected)>();
            foreach (var (text, selected, set) in choices)
            {
                var item = new ToolStripMenuItem(text);
                item.Click += async (_, _) =>
                {
                    set();
                    await _preferences.SaveAsync(TrayData.SettingsPath);
                    Balloon("Encoder experiment", $"{_preferences.HookRateControl.ToUpperInvariant()}, P{_preferences.HookPreset}, VBV {_preferences.HookVbvFrames} frame(s) is used the next time you arm H.264.", ToolTipIcon.Info);
                };
                items.Add((item, selected));
                group.DropDownItems.Add(item);
            }
            group.DropDownOpening += (_, _) => { foreach (var (item, selected) in items) item.Checked = selected(); };
            return group;
        }
        _rateMenu.DropDownItems.AddRange([
            Group("Rate control", [("VBR (default)", () => _preferences.HookRateControl == "vbr", () => _preferences.HookRateControl = "vbr"),
                                   ("CBR", () => _preferences.HookRateControl == "cbr", () => _preferences.HookRateControl = "cbr")]),
            Group("Preset", [..new[] { 1, 2, 4, 6, 7 }.Select<int, (string, Func<bool>, Action)>(p =>
                ($"P{p}" + (p == 2 ? " (default)" : ""), () => _preferences.HookPreset == p, () => _preferences.HookPreset = p))]),
            Group("VBV buffer", [..new[] { 1, 2, 4, 8, 16 }.Select<int, (string, Func<bool>, Action)>(v =>
                ($"{v} frame{(v == 1 ? "" : "s")}" + (v == 2 ? " (default)" : ""), () => _preferences.HookVbvFrames == v, () => _preferences.HookVbvFrames = v))]),
        ]);
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        // Windows is signing out or shutting down: ask the supervisor to restore now (it also notices the tray vanish).
        try { _controller.RequestCloseNoWait(); } catch (Exception) { }
    }

    private async Task ChangeTargetAsync(int mbps) =>
        Report(await _controller.ChangeTargetAsync(mbps, AskPassword, ConfirmPatch, ConfirmRestart));

    private void Report(ChangeOutcome outcome)
    {
        switch (outcome)
        {
            case ChangeOutcome.AppliedLive: Balloon("Bitrate changed", _controller.Current.Message, ToolTipIcon.Info); break;
            case ChangeOutcome.Reapplied when _controller.Current.State is TrayState.Armed or TrayState.Waiting: Balloon("H.264 re-applied", _controller.Current.Message, ToolTipIcon.Info); break;
            case ChangeOutcome.Declined: Balloon("Saved", "The new setting is used the next time you arm H.264.", ToolTipIcon.Info); break;
        }
    }

    private static Task<string?> AskPassword() =>
        Task.FromResult(PromptDialog.Ask("Frame password", "Enter the Frame user password. It is kept in Windows Credential Manager (encrypted for this Windows account) so H.264 can come back after SteamVR or the PC restarts, and is stored only once the Frame accepts it.", true));

    private static Task<bool> ConfirmArmRestart() => Task.FromResult(MessageBox.Show(
        "SteamVR is running. Arming H.264 restarts SteamVR once (your session will be interrupted). Restart SteamVR now?\n\nClose SteamVR and choose No to arm without a restart: it will wait for SteamVR to start.",
        "Restart SteamVR?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2, 0) == DialogResult.Yes);

    private static Task<bool> ConfirmRestart() => Task.FromResult(MessageBox.Show(
        "This change needs SteamVR to restart (your session will be interrupted). Restart SteamVR now?\n\nChoose No to use the new setting the next time you arm H.264.",
        "Restart SteamVR?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2, 0) == DialogResult.Yes);

    private void ShowSetup()
    {
        if (_setupForm is { IsDisposed: false }) { _setupForm.Activate(); return; }
        _setupForm = new SetupForm(_setup, _preferences);
        _setupForm.Show();
    }

    private async Task ArmAsync()
    {
        if (!_setup.IsConfigured()) { ShowSetup(); Balloon("Setup needed", "Finish the connection check, then arm H.264.", ToolTipIcon.Info); return; }
        await _controller.ArmAsync(AskPassword, ConfirmPatch, ConfirmArmRestart);
        if (_controller.Current.State is TrayState.Armed or TrayState.Waiting)
            Balloon("H.264 armed", _controller.Current.Message, ToolTipIcon.Info);
    }

    private Task<bool> ConfirmPatch(BitratePatchStatus status)
    {
        var answer = MessageBox.Show($"Bitrates above {TrayController.PatchFreeMaxMbps} Mbps need a small patch to SteamVR's Steam Link driver (driver_vrlink.dll). A verified backup of the original is kept and the patch can be removed from this menu.\n\n" +
            (status.VrServerRunning ? "SteamVR is running; close it first, then arm again.\n\n" : "") + "Install the patch now?",
            "Bitrate patch", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1, 0);
        return Task.FromResult(answer == DialogResult.Yes);
    }

    private async Task DisarmAsync() => await _controller.DisarmAsync(
        () => Task.FromResult(PromptDialog.Ask("Frame password", "The Frame password is needed to restore SteamVR's stock settings.", true)));

    private async Task SetPasswordAsync()
    {
        var text = PromptDialog.Ask("Frame password", "Enter the Frame user password to store in Windows Credential Manager.", true);
        if (string.IsNullOrEmpty(text)) return;
        _controller.SavePassword(text);
        Balloon("Frame password", "Stored. It is checked against the Frame the next time you arm.", ToolTipIcon.Info);
    }

    private async Task RefreshPatchMenuAsync()
    {
        try
        {
            var state = await _patch.InspectAsync();
            _patchState.Text = state.IsPatched ? "Installed: up to 500 Mbps" : state.IsSupported ? "Not installed (232 Mbps maximum)" : "Driver not recognised";
            _installPatch.Enabled = state.CanApply;
            _removePatch.Enabled = state.CanRestore;
            _installPatch.ToolTipText = _removePatch.ToolTipText = state.VrServerRunning ? "Close SteamVR first." : "";
        }
        catch (Exception error) { _patchState.Text = "Could not check: " + error.Message; _installPatch.Enabled = _removePatch.Enabled = false; }
    }

    private async Task PatchAsync(bool install)
    {
        try
        {
            var state = await _patch.InspectAsync();
            if (state.VrServerRunning) throw new InvalidOperationException("Close SteamVR first.");
            var done = install ? await _patch.ApplyAsync() : await _patch.RestoreAsync();
            Balloon("Bitrate patch", install ? (done.IsPatched ? "Installed. Bitrates up to 500 Mbps are available." : done.Message) : (done.IsPatched ? done.Message : "Removed. SteamVR is back to its own ceiling."), ToolTipIcon.Info);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Windows denied access to SteamVR's driver folder. Use \"Restart as administrator\" in this menu, then try again.");
        }
    }

    private void ToggleRecording()
    {
        try
        {
            if (_statsService.Recording)
            {
                var path = _statsService.LogPath;
                _statsService.StopRecording();
                Balloon("Stats log saved", path ?? "", ToolTipIcon.Info);
            }
            else Balloon("Recording stats", _statsService.StartRecording(RunHeader()), ToolTipIcon.Info);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Balloon("Stats log", error.Message, ToolTipIcon.Error); }
    }

    /// <summary>The setup being measured, written at the top of the log so a run explains itself.</summary>
    private IEnumerable<string> RunHeader()
    {
        var snapshot = _controller.Current;
        var lines = new List<string>
        {
            "SteamFrame Tray run log", "started " + DateTimeOffset.Now.ToString("O"), $"tray state {snapshot.State}: {snapshot.Message}",
            $"hook rate control {_preferences.HookRateControl.ToUpperInvariant()}, preset P{_preferences.HookPreset}, VBV {_preferences.HookVbvFrames} frame(s) of the average bitrate",
            $"tray target {snapshot.TargetMbps} Mbps", "armed H.264 " + (snapshot.IsArmed ? "yes" : "no"),
        };
        try
        {
            if (OpenVrSettings.IsRunning)
            {
                var live = OpenVrSettings.Read(_paths);
                lines.Add($"SteamVR targetBandwidth {live.Target:0} Mbps, automatic bandwidth {live.Automatic}, multiple links {live.MultipleLinks}, stream width {live.StreamWidth} (automatic {live.AutomaticWidth}), starting rate {live.StartingMbit?.ToString() ?? "default"}");
            }
            else lines.Add("SteamVR was not running when the log started");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException) { lines.Add("SteamVR settings could not be read: " + error.Message); }
        lines.Add("columns: mbps is measured encoder output; enc_* is what the encoder was configured with; held_* are frames the sender held back; comp_* are PC compositor counters");
        return lines;
    }

    private void ToggleStats()
    {
        if (_stats.Visible) _stats.Hide();
        else _stats.Show();
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) { Balloon("SteamFrame Tray", "Another action is still running.", ToolTipIcon.Info); return; }
        _busy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Balloon("SteamFrame Tray", error.Message, ToolTipIcon.Error); }
        finally { _busy = false; }
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        if (_controller.Current.IsArmed)
        {
            // H.264 is kept only while the tray is running and armed. If the Frame cannot be reached right now the
            // supervisor notices the tray is gone and keeps retrying the restore for ten minutes.
            try { await DisarmAsync(); }
            catch (Exception error) { Balloon("SteamFrame Tray", "H.264 will be released in the background: " + error.Message, ToolTipIcon.Warning); }
        }
        ExitApp();
    }

    private void ExitApp()
    {
        _poll.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _stats.Dispose();
        _statsService.Dispose();
        SystemEvents.SessionEnding -= OnSessionEnding;
        ExitThread();
    }

    private void Render(TraySnapshot snapshot)
    {
        _icon.Icon = TrayIcons.For(snapshot.State);
        _icon.Text = Truncate("SteamFrame: " + snapshot.Message, 127);
        _status.Text = snapshot.Message;
        _status.ToolTipText = snapshot.Message;
        _toggle.Text = snapshot.IsArmed ? "Disarm (back to SteamVR defaults)" : "Arm H.264";
        _toggle.Enabled = snapshot.State is not (TrayState.Arming or TrayState.Disarming);
        _menuSlider.SetValue(snapshot.TargetMbps);
    }

    private void Balloon(string title, string text, ToolTipIcon icon)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = Truncate(text, 250);
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(6000);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
