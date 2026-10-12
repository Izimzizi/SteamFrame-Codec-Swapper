using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private readonly CodecController _codecController = new();
    private CodecServiceStatus _codecService = CodecServiceStatus.NotStarted;
    private bool _codecSwitching;

    private bool CodecHooksPaused(CodecServiceStatus status) => _codecSwitching || status.State is "starting" or "switching" || !status.Live && !status.CanStart;

    private async Task EnsureCodecStableAsync()
    {
        var status = await _codecController.ReadStatusAsync();
        if (CodecHooksPaused(status)) throw new InvalidOperationException("Wait for the codec session to finish reconnecting before changing encoder controls.");
    }

    private void InitializeCodecControls()
    {
        CodecSelectionInput.SelectedIndex = Enum.IsDefined(_preferences.RequestedCodec) ? (int)_preferences.RequestedCodec : 0;
        CodecSessionTargetInput.Value = Math.Clamp(_preferences.CodecTargetMbps, 25, 200);
        CodecRefreshInput.SelectedIndex = _preferences.CodecFps == 120 ? 1 : 0;
        RenderCodecService(_codecService);
    }

    private void CodecSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Selection is a request; only the explicit button changes the runtime.
        if (SwitchCodecButton is not null) RefreshCodecButtons();
    }

    private void RenderCodecService(CodecServiceStatus status)
    {
        _codecService = status;
        CodecSessionStatus.Text = status.Message + (status.Live ? $" Service mode: {status.ActiveCodec} • {status.TargetMbps} Mbps request • {status.Fps} Hz." : "");
        var authenticationNeeded = status.CanStart && !status.Live;
        CodecPasswordInput.Visibility = CodecAuthenticationHint.Visibility = authenticationNeeded ? Visibility.Visible : Visibility.Collapsed;
        if (!authenticationNeeded) CodecPasswordInput.Password = "";
        CodecSwitchProgress.IsActive = _codecSwitching || status.Busy;
        RefreshCodecButtons();
    }

    private void RefreshCodecButtons()
    {
        var idle = !_actionPending && !_codecSwitching && !_codecService.Busy;
        SwitchCodecButton.IsEnabled = idle && (_codecService.CanStart || _codecService.Live && _codecService.State != "closed");
        CloseCodecButton.IsEnabled = idle && _codecService.Live && _codecService.State != "closed";
        CodecSelectionInput.IsEnabled = CodecSessionTargetInput.IsEnabled = CodecRefreshInput.IsEnabled = CodecPasswordInput.IsEnabled = idle;
        if (!_actionPending)
        {
            var encoderControls = !CodecHooksPaused(_codecService);
            ApplyButton.IsEnabled = MonitorButton.IsEnabled = StopMonitorButton.IsEnabled = RecoverButton.IsEnabled = encoderControls;
            AdvancedApplyButton.IsEnabled = AdvancedRestoreButton.IsEnabled = encoderControls;
            SupersampleFilteringInput.IsEnabled = encoderControls;
        }
    }

    private void MarkCodecReconnect()
    {
        CodecReadback.Text = "Codec: Reconnecting";
        CodecDetail.Text = "Waiting for verified output after the codec switch.";
        CodecCurrentReadback.Text = "Current encoder: reconnecting";
        VideoRate.Text = "—";
        VideoDetail.Text = "Stream reconnecting…";
        _rate.Reset(); _quantizers.Reset();
    }

    private async void SwitchCodec_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        if (!double.IsFinite(CodecSessionTargetInput.Value) || CodecSessionTargetInput.Value % 1 != 0)
            throw new ArgumentException("Use a whole-number codec-session target from 25 to 200 Mbps.");
        var request = new CodecSwitchRequest((StreamCodec)CodecSelectionInput.SelectedIndex, checked((int)CodecSessionTargetInput.Value), CodecRefreshInput.SelectedIndex == 1 ? 120 : 90);
        request.Validate();
        ReadConnectionInputs();
        var password = CodecPasswordInput.Password;
        CodecPasswordInput.Password = "";
        _codecSwitching = true;
        MarkCodecReconnect();
        CodecSwitchProgress.IsActive = true;
        try
        {
            var before = await _probe.GetSnapshotAsync();
            if (before.Installed && !before.PairedCodecTrialPresent) await _probe.StopAsync();
            var status = await _codecController.SwitchAsync(request, _preferences, password, new Progress<CodecServiceStatus>(RenderCodecService));
            _preferences.RequestedCodec = request.Codec;
            _preferences.CodecTargetMbps = request.TargetMbps;
            _preferences.CodecFps = request.Fps;
            await _preferences.SaveAsync();
            RenderCodecService(status);
            _configuredPid = 0;
            return $"{request.CodecName} codec session is ready. Reconnect the Frame if needed; the dashboard will verify fresh output.";
        }
        finally { password = ""; _codecSwitching = false; CodecSwitchProgress.IsActive = false; }
    });

    private async void CloseCodec_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        _codecSwitching = true;
        MarkCodecReconnect();
        CodecSwitchProgress.IsActive = true;
        try
        {
            var status = await _codecController.CloseAsync(new Progress<CodecServiceStatus>(RenderCodecService));
            _preferences.RequestedCodec = StreamCodec.Hevc;
            await _preferences.SaveAsync();
            CodecSelectionInput.SelectedIndex = 0;
            RenderCodecService(status);
            _configuredPid = 0;
            return "Codec session ended and HEVC restored. The dashboard will confirm the current encoder after reconnecting.";
        }
        finally { _codecSwitching = false; CodecSwitchProgress.IsActive = false; }
    });
}
