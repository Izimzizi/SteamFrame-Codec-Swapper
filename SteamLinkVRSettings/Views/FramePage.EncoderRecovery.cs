using Microsoft.UI.Xaml.Controls;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private bool _encoderRestartRequired;
    private bool _encoderActionError;

    private void RenderEncoderRecovery(EncoderSnapshot snapshot)
    {
        _encoderRestartRequired = snapshot.RequiresSteamVrRestart;
        EncoderRestartNotice.IsOpen = _encoderRestartRequired;
        if (_encoderActionError && snapshot.Installed && !snapshot.CodecReadFailed && !_encoderRestartRequired)
        {
            ActionStatus.IsOpen = false;
            _encoderActionError = false;
        }
        MonitorButton.Content = _encoderRestartRequired ? "Restart SteamVR and monitor" : "Start monitoring";
        ToolTipService.SetToolTip(MonitorButton, _encoderRestartRequired
            ? "Restarts SteamVR once, then resumes monitoring. The headset disconnects and a running game may close."
            : null);
    }

    private async Task<EncoderSnapshot> ReapplySavedEncodingAsync(EncoderSnapshot snapshot)
    {
        // Saved tuning only: selecting an unapplied preset must not activate it here.
        snapshot = await _probe.SetQualityAsync(_preferences.QualityEnabled, _preferences.GazeOffset, _preferences.PeripheralOffset);
        var policy = new EncodingPolicy(_preferences.RateControl, _preferences.MultiPass);
        // Arm all requests before refreshing. A fresh server may be waiting for
        // the Frame; hooks still need to be ready for encoder initialization.
        if (policy.IsValid) snapshot = await _probe.SetEncodingPolicyAsync(policy);
        if (snapshot.AdvancedEncoding is { Supported: true })
            snapshot = await _probe.SetAdvancedEncodingAsync(_preferences.AdvancedEncoding ?? new());
        _configuredPid = snapshot.ProcessId;
        if (snapshot.Active)
        {
            try
            {
                await Task.Run(() => OpenVrSettings.RefreshEncoder(_paths));
                snapshot = await _probe.GetSnapshotAsync();
            }
            catch (Exception error)
            {
                ShowStatus("Saved encoder requests are armed; live refresh unavailable: " + error.Message + " Connect the Frame to finish applying them.", false);
            }
        }
        return snapshot;
    }
}
