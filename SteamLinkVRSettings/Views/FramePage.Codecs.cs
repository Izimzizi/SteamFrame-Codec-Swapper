using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private readonly CodecController _codecController = new();
    private CodecServiceStatus _codecService = CodecServiceStatus.NotStarted;
    private bool _codecSwitching;
    private bool _syncingCodec = true;
    private bool _codecEdited;
    private bool _refreshEdited;
    private StreamCodec SelectedCodec => (StreamCodec)Math.Clamp(CodecSelectionInput.SelectedIndex, 0, 2);

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
        CodecRefreshInput.SelectedIndex = _preferences.CodecFps == 120 ? 1 : 0;
        _syncingCodec = false;
        RenderCodecService(_codecService);
    }
    private void CodecSelection_Changed(object sender, SelectionChangedEventArgs e)
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
                BitrateInput.Value = status.TargetMbps; CodecRefreshInput.SelectedIndex = status.Fps == 120 ? 1 : 0;
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
        var authenticationNeeded = paired && (_codecService.CanStart && !_codecService.Live || patchChange && _codecService.Live);
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
        AutomaticInput.IsEnabled = StartingBandwidthInput.IsEnabled = idle && !paired;
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
        var request = new CodecSwitchRequest(SelectedCodec, checked((int)BitrateInput.Value), CodecRefreshInput.SelectedIndex == 1 ? 120 : 90);
        request.Validate(); ReadConnectionInputs();
        var password = suppliedPassword ?? CodecPasswordInput.Password; CodecPasswordInput.Password = "";
        _codecSwitching = true; MarkCodecReconnect();
        try
        {
            var before = await _probe.GetSnapshotAsync();
            if (before.Installed && !before.PairedCodecTrialPresent) await _probe.StopAsync();
            var status = await _codecController.SwitchAsync(request, _preferences, password, new Progress<CodecServiceStatus>(RenderCodecService));
            _preferences.RequestedCodec = request.Codec; _preferences.CodecTargetMbps = request.TargetMbps; _preferences.CodecFps = request.Fps;
            await _preferences.SaveAsync(); RenderCodecService(status); _configuredPid = 0;
            return $"{request.CodecName} connected. Reopen the game if it closed; Overview verifies fresh output.";
        }
        finally { password = ""; _codecSwitching = false; }
    }
    private async Task EndCodecSessionAsync()
    {
        _codecSwitching = true; MarkCodecReconnect();
        try
        {
            RenderCodecService(await _codecController.CloseAsync(new Progress<CodecServiceStatus>(RenderCodecService)));
            _preferences.RequestedCodec = StreamCodec.Hevc; await _preferences.SaveAsync(); _configuredPid = 0;
        }
        finally { _codecSwitching = false; }
    }
    private async Task WaitForHevcSettingsAsync()
    {
        await OpenVrSettings.WaitUntilAvailableAsync(_paths);
    }
}
