using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private readonly CodecController _codecController = new();
    private readonly FrameCredentialStore _credentials = new();
    private bool _passwordStored;
    private CodecServiceStatus _codecService = CodecServiceStatus.NotStarted;
    private bool _codecSwitching;
    private bool _syncingCodec = true;
    private bool _codecEdited;
    private bool _refreshEdited;
    private StreamCodec SelectedCodec => (StreamCodec)Math.Clamp(CodecSelectionInput.SelectedIndex, 0, 2);
    /// <summary>The refresh choice as a rate: 0 keeps the display rate as it is.</summary>
    private int RefreshChoice => CodecRefreshInput.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var hz) ? hz : 90;
    /// <summary>The 90/120 Hz view that the benchmark and saved presets use.</summary>
    private int LegacyRefreshHz => RefreshChoice is 90 or 120 ? RefreshChoice : 90;
    private void SelectRefresh(int hz)
    {
        foreach (var item in CodecRefreshInput.Items.OfType<ComboBoxItem>())
            if (item.Tag is string tag && tag == hz.ToString()) { CodecRefreshInput.SelectedItem = item; return; }
        CodecRefreshInput.SelectedIndex = 0;
    }
    private bool StoredPasswordExists()
    {
        try { return _credentials.Exists; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private bool CodecHooksPaused(CodecServiceStatus status) => _codecSwitching || status.State is "starting" or "switching" || !status.Live && !status.CanStart;
    private async Task EnsureCodecStableAsync()
    {
        if (CodecHooksPaused(await _codecController.ReadStatusAsync())) throw new InvalidOperationException("Wait for the codec session to finish reconnecting before changing settings.");
    }
    private void InitializeCodecControls()
    {
        _syncingCodec = true;
        CodecSelectionInput.SelectedIndex = Enum.IsDefined(_preferences.RequestedCodec) ? (int)_preferences.RequestedCodec : 0;
        if (SelectedCodec != StreamCodec.Hevc) BitrateInput.Value = Math.Clamp(_preferences.CodecTargetMbps, 25, CodecSwitchRequest.MaximumMbpsFor(SelectedCodec));
        SelectRefresh(_preferences.CodecFps);
        FixedBitrateInput.IsChecked = _preferences.CodecFixedBitrate;
        KeepH264Input.IsChecked = _preferences.KeepH264;
        _passwordStored = StoredPasswordExists();
        _syncingCodec = false;
        RenderCodecService(_codecService);
    }
    private void CodecSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCodec || ApplyButton is null) return;
        _codecEdited = true; _bandwidthEdited = true; _basicEditRevision++;
        RefreshCodecButtons(); UpdateStreamEditStatus();
    }
    private void KeepH264_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingCodec || ApplyButton is null) return;
        _codecEdited = true; _bandwidthEdited = true; _basicEditRevision++;
        RefreshCodecButtons(); UpdateStreamEditStatus();
    }
    private async void ForgetPassword_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _credentials.Delete(); _passwordStored = false;
            _preferences.KeepH264 = false; await _preferences.SaveAsync();
            _syncingCodec = true;
            try { KeepH264Input.IsChecked = false; }
            finally { _syncingCodec = false; }
            ShowStatus(_codecService.Live && _codecService.Keep
                ? "The stored Frame password was removed. The running session continues until you switch back to HEVC, but it will not resume after a restart."
                : "The stored Frame password was removed.", false);
            RefreshCodecButtons();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            ShowStatus("The stored Frame password could not be removed: " + error.Message, true);
        }
    }
    /// <summary>After the app or PC restarted, take the kept H.264 session up again without touching SteamVR.</summary>
    private async Task TryResumeKeptSessionAsync()
    {
        if (!_preferences.KeepH264 || _preferences.RequestedCodec != StreamCodec.H264 || _codecSwitching) return;
        string? password = null;
        try
        {
            password = _credentials.Load();
            if (string.IsNullOrEmpty(password)) return;
            var status = await _codecController.ReadStatusAsync();
            if (status.Live || !status.CanStart) return;
            var fps = _preferences.CodecFps is 0 or 72 or 80 or 90 or 120 ? _preferences.CodecFps : 90;
            var request = new CodecSwitchRequest(StreamCodec.H264, Math.Clamp(_preferences.CodecTargetMbps, 25, 500), fps, _preferences.CodecFixedBitrate);
            ReadConnectionInputs();
            _codecSwitching = true; RefreshCodecButtons();
            RenderCodecService(await _codecController.SwitchAsync(request, _preferences, password, new Progress<CodecServiceStatus>(RenderCodecService), keep: true, resume: true));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowStatus("The kept H.264 session could not resume: " + error.Message, true);
        }
        finally { password = null; _codecSwitching = false; RefreshCodecButtons(); }
    }
    private void FixedBitrate_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingCodec || ApplyButton is null) return;
        _codecEdited = true; _bandwidthEdited = true; _basicEditRevision++;
        RefreshCodecButtons(); UpdateStreamEditStatus();
    }
    private void CodecRefresh_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCodec || ApplyButton is null) return;
        _codecEdited = _refreshEdited = true; _basicEditRevision++; UpdateStreamEditStatus();
        RefreshCodecButtons();
    }
    private void RenderCodecService(CodecServiceStatus status)
    {
        _codecService = status;
        CodecSessionStatus.Text = status.Live ? status.Message : "HEVC uses the standard SteamVR path. Experimental codecs start a temporary paired session.";
        if (!_codecEdited && !_bandwidthEdited && status.Live && status.State == "ready")
        {
            _syncingCodec = _syncingBandwidth = true;
            try
            {
                CodecSelectionInput.SelectedIndex = status.ActiveCodec switch { "AV1" => 1, "H.264" => 2, _ => 0 };
                BitrateInput.Maximum = CodecSwitchRequest.MaximumMbpsFor(SelectedCodec);
                BitrateInput.Value = status.TargetMbps; SelectRefresh(status.RefreshKept ? 0 : status.Fps);
                FixedBitrateInput.IsChecked = status.FixedBitrate;
                if (SelectedCodec == StreamCodec.H264) KeepH264Input.IsChecked = status.Keep;
            }
            finally { _syncingCodec = _syncingBandwidth = false; }
        }
        CodecSwitchProgress.IsActive = _codecSwitching || status.Busy;
        RefreshCodecButtons();
    }
    private void RefreshCodecButtons()
    {
        if (ApplyButton is null) return;
        var idle = !_actionPending && !_codecSwitching && !_sweepRunning && !CodecHooksPaused(_codecService);
        var paired = SelectedCodec != StreamCodec.Hevc;
        var patchChange = _pendingPreset != null && _pendingPreset.BitratePatch != (_patch?.IsPatched == true);
        var keepWanted = SelectedCodec == StreamCodec.H264 && KeepH264Input.IsChecked == true;
        var authenticationNeeded = paired && !_passwordStored && (_codecService.CanStart && !_codecService.Live || patchChange && _codecService.Live || keepWanted && !_codecService.Keep);
        CodecPasswordInput.Visibility = CodecAuthenticationHint.Visibility = authenticationNeeded ? Visibility.Visible : Visibility.Collapsed;
        if (!authenticationNeeded) CodecPasswordInput.Password = "";
        BitrateInput.Maximum = CodecSwitchRequest.MaximumMbpsFor(SelectedCodec);
        CodecModeHint.Text = SelectedCodec switch
        {
            StreamCodec.Av1 => "AV1: 25–200 Mbps, 8-bit hardware encoding and decoding. Changes to codec, bitrate or refresh reconnect SteamVR. Start with 100 Mbps at 90 Hz.",
            StreamCodec.H264 => "H.264: 25–500 Mbps. The packed image is smaller than AV1 (1024 × 4096). 225 Mbps was the highest clean trial; above 232 Mbps needs the bitrate patch. SteamVR can lower the rate after link problems. Changes reconnect SteamVR.",
            _ => "HEVC: 25–500 Mbps. Targets above the stock ceiling need the bitrate patch. Refresh changes are sent to the Frame and verified against the active PC display rate."
        };
        ApplyButton.Content = paired || _codecService.Live || patchChange || _refreshEdited ? "Apply and reconnect if needed" : "Apply stream settings";
        ApplyButton.IsEnabled = idle;
        CodecSelectionInput.IsEnabled = CodecPasswordInput.IsEnabled = BitrateInput.IsEnabled = idle;
        CodecRefreshInput.IsEnabled = idle;
        FixedBitrateInput.Visibility = FixedBitrateHint.Visibility = SelectedCodec == StreamCodec.H264 ? Visibility.Visible : Visibility.Collapsed;
        FixedBitrateInput.IsEnabled = idle;
        KeepH264Input.Visibility = KeepH264Hint.Visibility = SelectedCodec == StreamCodec.H264 ? Visibility.Visible : Visibility.Collapsed;
        KeepH264Input.IsEnabled = idle;
        ForgetPasswordButton.Visibility = _passwordStored ? Visibility.Visible : Visibility.Collapsed;
        ForgetPasswordButton.IsEnabled = idle;
        AutomaticInput.IsEnabled = idle && !paired;
        StartingBandwidthInput.IsEnabled = idle && !paired && AutomaticInput.IsOn;
        StreamWidthInput.IsEnabled = idle && !paired && !AutomaticWidthInput.IsOn;
        AutomaticWidthInput.IsEnabled = QualityInput.IsEnabled = idle && !paired;
        GazeInput.IsEnabled = PeripheralInput.IsEnabled = idle && !paired && QualityInput.IsOn;
        AutomaticOptions.Visibility = paired ? Visibility.Collapsed : Visibility.Visible;
        HevcPictureOptions.Visibility = paired ? Visibility.Collapsed : Visibility.Visible;
        AmfInput.IsEnabled = MultipleLinksInput.IsEnabled = idle && !paired;
        SupersampleFilteringInput.IsEnabled = idle;
        RateControlInput.IsEnabled = MultiPassInput.IsEnabled = ReapplyInput.IsEnabled = idle && !paired;
        StockRestoreButton.IsEnabled = idle;
        PreviousProfileButton.IsEnabled = idle && _previousProfile != null;
        MonitorButton.IsEnabled = idle && !_monitorInstalled;
        RestartEncoderButton.IsEnabled = idle && _encoderRestartRequired && !_codecService.Live;
        StopMonitorButton.IsEnabled = idle && _monitorInstalled;
        RecoverButton.IsEnabled = idle;
        ReconnectFrameButton.IsEnabled = idle && !_codecService.Live;
        AdvancedApplyButton.IsEnabled = idle;
        AdvancedApplyButton.Content = _pendingPreset != null ? "Apply complete preset" : "Apply advanced settings";
        AdvancedRestoreButton.IsEnabled = idle && !paired;
    }
    private void UpdateStreamEditStatus()
    {
        if (StreamChangeStatus is null) return;
        StreamChangeStatus.Text = _bandwidthEdited || _codecEdited || _pendingPreset != null ? "Changes ready to apply" : "Current setup loaded";
    }
    private void MarkCodecReconnect()
    {
        CodecSessionStatus.Text = "Reconnecting SteamVR and the Frame…";
        CodecReadback.Text = "Codec: Reconnecting"; CodecCurrentReadback.Text = "Current encoder: reconnecting";
        CodecDetail.Text = "Waiting for verified output."; VideoRate.Text = "—"; VideoDetail.Text = "Reconnecting…";
        _rate.Reset(); _quantizers.Reset();
    }
    private async Task<string> ApplySelectedCodecAsync(string? suppliedPassword = null)
    {
        var keep = SelectedCodec == StreamCodec.H264 && KeepH264Input.IsChecked == true;
        var request = new CodecSwitchRequest(SelectedCodec, checked((int)BitrateInput.Value), RefreshChoice,
            SelectedCodec == StreamCodec.H264 && FixedBitrateInput.IsChecked == true);
        request.Validate(); ReadConnectionInputs();
        var typed = suppliedPassword ?? CodecPasswordInput.Password; CodecPasswordInput.Password = "";
        var password = string.IsNullOrEmpty(typed) ? _credentials.Load() : typed;
        _codecSwitching = true; MarkCodecReconnect();
        try
        {
            var before = await _probe.GetSnapshotAsync();
            if (before.Installed && !before.PairedCodecTrialPresent) await _probe.StopAsync();
            var running = await _codecController.ReadStatusAsync();
            if (running.Live && running.Keep != keep)
            {
                // Keeping H.264 is decided when a session starts, so changing it restarts the session.
                RenderCodecService(await _codecController.CloseAsync(new Progress<CodecServiceStatus>(RenderCodecService)));
                for (var attempt = 0; attempt < 60 && !(await _codecController.ReadStatusAsync()).CanStart; attempt++) await Task.Delay(500);
            }
            var status = await _codecController.SwitchAsync(request, _preferences, password, new Progress<CodecServiceStatus>(RenderCodecService), keep: keep);
            if (keep && !string.IsNullOrEmpty(typed)) { _credentials.Save(typed); _passwordStored = true; }
            _preferences.RequestedCodec = request.Codec; _preferences.CodecTargetMbps = request.TargetMbps; _preferences.CodecFps = request.Fps; _preferences.CodecFixedBitrate = request.FixedBitrate;
            _preferences.KeepH264 = keep;
            await _preferences.SaveAsync(); RenderCodecService(status); _configuredPid = 0;
            return $"{request.CodecName} connected. Reopen the game if it closed; Overview verifies fresh output.";
        }
        finally { password = ""; typed = ""; _codecSwitching = false; }
    }
    private async Task EndCodecSessionAsync()
    {
        _codecSwitching = true; MarkCodecReconnect();
        try
        {
            RenderCodecService(await _codecController.CloseAsync(new Progress<CodecServiceStatus>(RenderCodecService)));
            _preferences.RequestedCodec = StreamCodec.Hevc; _preferences.KeepH264 = false; await _preferences.SaveAsync(); _configuredPid = 0;
        }
        finally { _codecSwitching = false; }
    }
    private async Task WaitForHevcSettingsAsync()
    {
        await OpenVrSettings.WaitUntilAvailableAsync(_paths);
    }
}
