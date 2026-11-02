using System.Diagnostics;
using Microsoft.Win32;
using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>The notification-area icon and its menu. All state lives in <see cref="TrayController"/>.</summary>
public sealed partial class TrayApp : ApplicationContext
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
    private readonly ToolStripMenuItem _record = new("Record stream log");
    private readonly ToolStripMenuItem _resetCounters = new("Reset dropped-frame counters");
    private HealthReader? _healthReader;
    private readonly NotifyIcon _icon = new() { Visible = true };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
    private readonly ToolStripMenuItem _status = new() { Enabled = false, AutoSize = false, Size = new Size(MenuBitrateSlider.MenuWidth, 24), TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripMenuItem _useH264 = new("H.264");
    private readonly ToolStripMenuItem _useDefault = new("Default (HEVC)");
    private readonly MenuBitrateSlider _menuSlider;
    private readonly ToolStripMenuItem _rateMenu = new("Encoder settings");
    private readonly ToolStripLabel _bitrateLabel = new() { AutoSize = false, Size = new Size(MenuBitrateSlider.MenuWidth, 22), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
    private readonly ToolStripMenuItem _statsItem = new("Stream monitor");
    private readonly ToolStripMenuItem _startup = new("Start with Windows");
    private readonly ToolStripMenuItem _password = new("Frame password");
    private readonly ToolStripMenuItem _setPassword = new("Set or change…");
    private readonly ToolStripMenuItem _forgetPassword = new("Forget Frame password");
    private readonly ToolStripMenuItem _patchMenu = new("Bitrate limit patch (up to 500 Mbps)");
    private readonly ToolStripMenuItem _patchState = new() { Enabled = false };
    private readonly ToolStripMenuItem _installPatch = new("Install patch");
    private readonly ToolStripMenuItem _removePatch = new("Remove patch");
    private readonly ToolStripMenuItem _admin = new("Restart as administrator");
    private readonly ToolStripMenuItem _driverMenu = new("SteamVR driver");
    private readonly SteamVrDriver _driver;
    private readonly SetupCheck _setup;
    private SetupForm? _setupForm;
    private bool _busy, _exiting;

    public TrayApp(bool autostart)
    {
        _codec = new CodecController(sessionDirectory: AppData.SessionDirectory, steamPaths: _paths);
        _patch = new DriverPatch(new BitratePatcher(), _paths.Driver);
        _liveBitrate = new LiveBitrate(_paths);
        _driver = SteamVrDriver.ForThisPc(_paths);
        // A driver left over from an older build must never meet a newer tray (the two would disagree about the hook): bring it up to date while SteamVR is closed.
        var driverBehind = false;
        try
        {
            if (_driver.Inspect() == SteamVrDriverState.Outdated)
            {
                if (OpenVrSettings.IsRunning) driverBehind = true;   // SteamVR has the old DLL open; it can only be replaced once SteamVR is closed
                else _driver.Install();
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException) { /* the SteamVR driver menu still offers it */ }
        _controller = new TrayController(new CodecServiceAdapter(_codec), new CredentialVault(new FrameCredentialStore()), _patch, _preferences, () => _preferences.SaveAsync(AppData.SettingsPath), _liveBitrate, () => _driver.Inspect() == SteamVrDriverState.Installed);
        _setup = new SetupCheck(new ProcessRunner(), _preferences, () => _preferences.SaveAsync(AppData.SettingsPath), new CredentialVault(new FrameCredentialStore()), SetupEnvironment.ForThisPc(_paths, Path.Combine(AppContext.BaseDirectory, "CodecLab")), _patch);
        _statsService = new StatsService(new StatsSampler(() => _probe.GetSnapshotAsync(), () => _probe.StartAsync(), health: (_healthReader = new HealthReader(_paths, AppData.DiagnosticsDirectory)).ReadAsync, monitorAllowed: () => !_controller.Current.IsArmed, timing: now => EncodeTiming.Read(AppData.EncodeTimingPath, now)), () => _controller.Current);
        _stats = new StatsForm(_statsService);
        _menuSlider = new MenuBitrateSlider(_bitrateLabel, mbps => RunAsync(() => ChangeTargetAsync(mbps)));
        BuildMenu();
        SystemEvents.SessionEnding += OnSessionEnding;
        _controller.Changed += Render;
        Render(_controller.Current);
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleStats(); };
        _poll.Tick += async (_, _) => { try { await _controller.RefreshAsync(); } catch (Exception) { /* the next poll retries */ } };
        _poll.Start();
        StartHud();
        if (driverBehind)
            Balloon("SteamVR driver is out of date", "SteamVR is running with an older codec driver than this tray. Close SteamVR, then restart this tray (or use SteamVR driver > Install), or codec changes may fail.", ToolTipIcon.Warning);
        _ = StartAsync(autostart);
    }

    private static FramePreferences LoadPreferences()
    {
        var preferences = FramePreferences.Load(AppData.SettingsPath);
        // The tray keeps its pinned host key with its own files, never in the full app's folder.
        if (string.IsNullOrWhiteSpace(preferences.SshKnownHosts) || !AppData.IsInside(preferences.SshKnownHosts)) preferences.SshKnownHosts = AppData.KnownHosts;
        return preferences;
    }

    private async Task StartAsync(bool autostart)
    {
        try { await _controller.StartAsync(); }
        catch (Exception error) { Balloon("SteamFrame Codec Swapper", error.Message, ToolTipIcon.Error); }
        if (!autostart && !_setup.IsConfigured()) ShowSetup();
        else if (!autostart && !_controller.Current.IsArmed && _controller.Current.State != TrayState.Error)
            Balloon("SteamFrame Codec Swapper", "Right-click the icon to choose H.264.", ToolTipIcon.Info);
    }

    private void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        BuildRateMenu();
        _useH264.Click += async (_, _) => await RunAsync(() => _controller.Current.IsArmed ? Task.CompletedTask : ArmAsync());
        _useDefault.Click += async (_, _) => await RunAsync(() => _controller.Current.IsArmed ? DisarmAsync() : Task.CompletedTask);
        _statsItem.Click += (_, _) => ToggleStats();
        _record.Click += (_, _) => ToggleRecording();
        _resetCounters.Click += (_, _) => _healthReader?.ResetCounters();
        _startup.Click += (_, _) => { try { StartupRegistration.Set(!StartupRegistration.IsEnabled); } catch (Exception error) { Balloon("Start with Windows", error.Message, ToolTipIcon.Error); } };
        _setPassword.Click += async (_, _) => await RunAsync(SetPasswordAsync);
        _forgetPassword.Click += async (_, _) => await RunAsync(() =>
        {
            _controller.ForgetPassword();
            return Task.CompletedTask;
        });
        _password.DropDownItems.AddRange([_setPassword, _forgetPassword]);
        _installPatch.Click += async (_, _) => await RunAsync(() => PatchAsync(install: true));
        _removePatch.Click += async (_, _) => await RunAsync(() => PatchAsync(install: false));
        _admin.Click += (_, _) => { if (Elevation.RestartElevated()) ExitApp(); };
        _patchMenu.DropDownItems.AddRange([_patchState, new ToolStripSeparator(), _installPatch, _removePatch, _admin]);
        _patchMenu.DropDownOpening += async (_, _) => await RefreshPatchMenuAsync();
        BuildDriverMenu();
        var hudMenu = BuildHudMenu();
        var setup = new ToolStripMenuItem("Frame connection…");
        setup.Click += (_, _) => ShowSetup();
        var openLogs = new ToolStripMenuItem("Open logs folder");
        openLogs.Click += (_, _) => { try { Directory.CreateDirectory(AppData.LogsDirectory); Process.Start(new ProcessStartInfo(AppData.LogsDirectory) { UseShellExecute = true }); } catch (Exception) { } };
        var folder = new ToolStripMenuItem("Open supervisor files");
        folder.Click += (_, _) => { try { Directory.CreateDirectory(_codec.SessionDirectory); Process.Start(new ProcessStartInfo(_codec.SessionDirectory) { UseShellExecute = true }); } catch (Exception) { } };
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += async (_, _) => await ExitAsync();
        menu.Items.AddRange([_status, new ToolStripSeparator(), _useH264, _useDefault, _bitrateLabel, _menuSlider, _rateMenu, new ToolStripSeparator(), _statsItem, _record, _resetCounters, hudMenu, openLogs,
            new ToolStripSeparator(), _startup, setup, _password, _patchMenu, _driverMenu, folder, new ToolStripSeparator(), exit]);
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
                    if (WarnWithoutDriver()) return;
                    set();
                    await SaveEncoderOptionsAsync();
                    try { await _controller.ApplyEncoderOptionsAsync(); }
                    catch (Exception error) when (error is not OperationCanceledException) { Balloon("Encoder settings", "Saved, but not applied to the running encoder: " + error.Message, ToolTipIcon.Warning); }
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

    /// <summary>Registers the hook as a SteamVR driver: loaded at every SteamVR start, streaming HEVC until H.264 is chosen.</summary>
    private void BuildDriverMenu()
    {
        var state = new ToolStripMenuItem { Enabled = false };
        var install = new ToolStripMenuItem("Install (or update)");
        var remove = new ToolStripMenuItem("Remove");
        void Refresh()
        {
            var current = _driver.Inspect();
            state.Text = current switch { SteamVrDriverState.Installed => "Installed: loads with SteamVR", SteamVrDriverState.Outdated => "Installed, but not this tray's version", _ => "Not installed" };
            install.Enabled = _driver.PackageAvailable && current != SteamVrDriverState.Installed;
            remove.Enabled = current != SteamVrDriverState.NotInstalled;
        }
        async Task Do(Action action)
        {
            try { await Task.Run(action); }
            catch (Exception error) when (error is not OperationCanceledException) { Balloon("SteamVR driver", error.Message, ToolTipIcon.Warning); }
            Refresh();
        }
        install.Click += async (_, _) => await RunAsync(() => Do(_driver.Install));
        remove.Click += async (_, _) => await RunAsync(() => Do(_driver.Remove));
        _driverMenu.DropDownOpening += (_, _) => Refresh();
        _driverMenu.DropDownItems.AddRange([state, new ToolStripSeparator(), install, remove]);
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        // Windows is signing out or shutting down: ask the supervisor to restore now (it also notices the tray vanish).
        try { _controller.RequestCloseNoWait(); } catch (Exception) { }
    }

    private async Task ChangeTargetAsync(int mbps)
    {
        await _controller.ChangeTargetAsync(mbps, AskPassword, ConfirmPatch);
        await WarnAboveStockCeilingAsync(mbps);
    }

    /// <summary>SteamVR accepts bitrates up to 350 Mbps, but its unpatched driver streams at most 232: the setting changes and the user is told it will not show yet.</summary>
    private async Task WarnAboveStockCeilingAsync(int mbps)
    {
        if (mbps <= TrayController.StockCeilingMbps) return;
        BitratePatchStatus state;
        try { state = await _patch.InspectAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { return; }
        if (state.IsPatched || !state.IsSupported) return;
        Balloon("Bitrate above " + TrayController.StockCeilingMbps + " Mbps", $"{mbps} Mbps is set, but SteamVR's unpatched driver streams at most {TrayController.StockCeilingMbps} Mbps. Close SteamVR, then use the tray menu: Bitrate limit patch > Install.", ToolTipIcon.Warning);
    }

    /// <summary>Rate control, preset and VBV reach the encoder only through the SteamVR driver; without it a change does nothing, so say so (on the HUD too). True when it did.</summary>
    private bool WarnWithoutDriver()
    {
        if (_driver.Inspect() == SteamVrDriverState.Installed) return false;
        Balloon("Encoder settings", "Encoder settings only work through the SteamVR driver, which is not installed (or not this version), so they have no effect yet. Close SteamVR, then use the tray menu: SteamVR driver > Install.", ToolTipIcon.Warning);
        return true;
    }

    private static Task<string?> AskPassword() =>
        Task.FromResult(PromptDialog.Ask("Frame password", "Enter the Frame user password. It is kept in Windows Credential Manager (encrypted for this Windows account) so H.264 can come back after SteamVR or the PC restarts, and is stored only once the Frame accepts it.", true));

    private void ShowSetup()
    {
        if (_setupForm is { IsDisposed: false }) { _setupForm.Activate(); return; }
        _setupForm = new SetupForm(_setup, _preferences);
        _setupForm.Show();
    }

    private async Task ArmAsync()
    {
        TrayLog("Arm requested");
        if (!_setup.IsConfigured()) { ShowSetup(); Balloon("Setup needed", "Finish the connection check, then choose H.264.", ToolTipIcon.Warning); return; }
        if (!await EnsureDriverAsync()) return;
        await _controller.ArmAsync(AskPassword, ConfirmPatch);
        if (_controller.Current.State is TrayState.Armed or TrayState.Waiting) await WarnAboveStockCeilingAsync(_controller.TargetMbps);
    }

    /// <summary>H.264 works through the SteamVR driver. Missing or out of date: offer to install it, or say what to do if SteamVR is holding the old one.</summary>
    private Task<bool> EnsureDriverAsync()
    {
        if (_driver.Inspect() == SteamVrDriverState.Installed) return Task.FromResult(true);
        if (!_driver.PackageAvailable) { Balloon("SteamVR driver", "This build does not include the SteamVR driver, which H.264 needs.", ToolTipIcon.Error); return Task.FromResult(false); }
        if (OpenVrSettings.IsRunning)
        {
            // Nothing can be installed while SteamVR has the driver open, and a dialog on the desktop is invisible in the headset: say it on both.
            Balloon("H.264 needs the SteamVR driver", "Close SteamVR, then choose H.264 again (or tray menu: SteamVR driver > Install).", ToolTipIcon.Warning);
            return Task.FromResult(false);
        }
        var answer = MessageBox.Show("H.264 needs the CodecSwapper SteamVR driver, which is " + (_driver.IsRegistered() ? "not this tray's version." : "not installed.") +
            " It is a small driver SteamVR loads at start to carry the H.264 encoder; it can be removed from the tray menu at any time.\n\nInstall it now?",
            "SteamVR driver", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1, 0);
        if (answer != DialogResult.Yes) return Task.FromResult(false);
        try { _driver.Install(); return Task.FromResult(true); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        { Balloon("SteamVR driver", error.Message, ToolTipIcon.Error); return Task.FromResult(false); }
    }

    private Task<bool> ConfirmPatch(BitratePatchStatus status)
    {
        if (status.VrServerRunning)
        {
            // The patch edits a file SteamVR has open, and a dialog on the desktop is invisible in the headset: say it on both.
            Balloon("Bitrate limit patch needed", $"Above {TrayController.PatchFreeMaxMbps} Mbps needs the bitrate patch. Close SteamVR, then use the tray menu: Bitrate limit patch > Install.", ToolTipIcon.Warning);
            return Task.FromResult(false);
        }
        var answer = MessageBox.Show($"Bitrates above {TrayController.PatchFreeMaxMbps} Mbps need a small patch to SteamVR's Steam Link driver (driver_vrlink.dll). A verified backup of the original is kept and the patch can be removed from this menu.\n\n" +
            "Install the patch now?",
            "Bitrate limit patch", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1, 0);
        return Task.FromResult(answer == DialogResult.Yes);
    }

    private async Task DisarmAsync()
    {
        TrayLog("Disarm requested");
        await _controller.DisarmAsync(
            () => Task.FromResult(PromptDialog.Ask("Frame password", "The Frame password is needed to restore SteamVR's stock settings.", true)));
    }

    /// <summary>Who asked for a codec change, so a switch that goes back by itself can be traced.</summary>
    private static void TrayLog(string what)
    {
        try
        {
            var callers = string.Join(" < ", new System.Diagnostics.StackTrace().GetFrames().Skip(2).Take(6).Select(f => f.GetMethod()?.Name ?? "?"));
            Directory.CreateDirectory(AppData.Root);
            File.AppendAllText(Path.Combine(AppData.Root, "tray.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + what + " (" + callers + ")" + Environment.NewLine);
        }
        catch (IOException) { /* diagnostics only */ }
    }

    private async Task SetPasswordAsync()
    {
        var text = PromptDialog.Ask("Frame password", "Enter the Frame user password to store in Windows Credential Manager.", true);
        if (string.IsNullOrEmpty(text)) return;
        _controller.SavePassword(text);
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
            if (done.IsPatched != install) Balloon("Bitrate limit patch", done.Message, ToolTipIcon.Warning);
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
            "SteamFrame Codec Swapper run log", "started " + DateTimeOffset.Now.ToString("O"), $"tray state {snapshot.State}: {snapshot.Message}",
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
        if (_busy) { Balloon("SteamFrame Codec Swapper", "Another action is still running.", ToolTipIcon.Warning); return; }
        _busy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Balloon("SteamFrame Codec Swapper", error.Message, ToolTipIcon.Error); }
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
            catch (Exception error) { Balloon("SteamFrame Codec Swapper", "H.264 will be released in the background: " + error.Message, ToolTipIcon.Warning); }
        }
        // A session kept open for fast switching ends with the tray (the supervisor also notices the tray is gone).
        try { _controller.RequestCloseNoWait(); } catch (Exception) { /* the supervisor closes itself */ }
        ExitApp();
    }

    private void ExitApp()
    {
        _poll.Stop();
        StopHud();
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
        _useH264.Checked = snapshot.IsArmed;
        _useDefault.Checked = !snapshot.IsArmed;
        _useH264.Enabled = _useDefault.Enabled = snapshot.State is not (TrayState.Arming or TrayState.Disarming);
        _menuSlider.SetValue(snapshot.TargetMbps);
    }

    private void Balloon(string title, string text, ToolTipIcon icon)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = Truncate(text, 250);
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(6000);
        ShowOnHud(title, text, icon);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
