using Microsoft.UI.Xaml;
using SteamLinkVRSettings.Frame;
using SteamLinkVRSettings.Services;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private bool _syncingSupersample = true;
    private bool _refreshVerifiedOnFrame;

    private SettingsService SteamVrVideoSettings() => new()
    {
        Section = OpenVrSettings.SteamVrVideoSection,
        UserConfigPath = _paths.Config,
        DefaultsPath = System.IO.Path.Combine(_paths.SteamVrRoot, "resources", "settings", "default.vrsettings")
    };

    private async Task LoadSteamVrVideoAsync()
    {
        try
        {
            var values = await SteamVrVideoSettings().LoadMergedAsync();
            if (!_codecEdited && _pendingPreset == null && SelectedCodec == StreamCodec.Hevc &&
                values.TryGetValue("preferredRefreshRate", out var refresh) && Convert.ToDouble(refresh) is 90 or 120)
            {
                _syncingCodec = true;
                try { CodecRefreshInput.SelectedIndex = Convert.ToDouble(refresh) == 120 ? 1 : 0; }
                finally { _syncingCodec = false; }
            }
            if (values.TryGetValue(OpenVrSettings.SupersampleFilteringKey, out var value) && value is bool enabled)
                ShowSupersampleValue(enabled, "Saved setting");
            else SupersampleFilteringReadback.Text = "Saved setting unavailable; start SteamVR for a live readback.";
        }
        finally { _syncingSupersample = false; }
    }

    private void ShowSupersampleValue(bool enabled, string source)
    {
        _syncingSupersample = true;
        try { SupersampleFilteringInput.IsOn = enabled; }
        finally { _syncingSupersample = false; }
        SupersampleFilteringReadback.Text = $"{source}: {(enabled ? "enabled" : "disabled")}";
    }

    private void SupersampleFiltering_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingSupersample) return;
        _bandwidthEdited = true; _basicEditRevision++; UpdateStreamEditStatus();
        SupersampleFilteringReadback.Text = "Ready to apply";
    }
    private async Task ApplySupersampleAsync()
    {
        var requested = SupersampleFilteringInput.IsOn;
        if (await Task.Run(() => OpenVrSettings.IsRunning))
        {
            var accepted = await Task.Run(() => OpenVrSettings.SetSupersampleFiltering(_paths, requested));
            ShowSupersampleValue(accepted, "Live SteamVR readback");
        }
        else
        {
            await SteamVrVideoSettings().SaveUserOverridesAsync(new() { [OpenVrSettings.SupersampleFilteringKey] = requested });
            ShowSupersampleValue(requested, "Saved for the next SteamVR session");
        }
    }

    private async Task ApplyHevcRefreshAsync(int fps)
    {
        ReadConnectionInputs();
        _refreshVerifiedOnFrame = false;
        var running = await Task.Run(() => OpenVrSettings.IsRunning);
        bool alreadyActive = false;
        if (running)
            try { alreadyActive = FrameDisplayRate.Matches(await ReadOpenVrAsync("display-hz", () => OpenVrSettings.ReadDisplayHz(_paths)), fps); }
            catch (IOException) { }
        RefreshReadback.Text = $"Requesting {fps} Hz on the Frame…";
        // Host preference comes first: an active stream can otherwise immediately
        // reassert the old rate over the receiver's requested display mode.
        if (running)
            await Task.Run(() => OpenVrSettings.SetPreferredRefreshRate(_paths, fps));
        else await SteamVrVideoSettings().SaveUserOverridesAsync(new() { ["preferredRefreshRate"] = (double)fps });
        // Applying a preset at an already active rate must not require a new SSH
        // pairing just to preserve that rate. A real mode change still does.
        if (alreadyActive) { RefreshReadback.Text = $"Headset already active at {fps} Hz; preference saved."; return; }
        await FrameDisplayRate.SetAsync(_preferences, fps, _lifetime?.Token ?? default);
        _refreshVerifiedOnFrame = true;
        RefreshReadback.Text = $"Frame verified at {fps} Hz; checking the PC stream…";
    }

    private async Task<string> VerifyHevcRefreshAsync(int fps)
    {
        if (!await Task.Run(() => OpenVrSettings.IsRunning))
            return $" Frame display verified at {fps} Hz; the PC will use it when SteamVR starts.";
        async Task<bool> WaitForRate()
        {
            for (int i = 0; i < 12; i++)
            {
                try { if (FrameDisplayRate.Matches(await ReadOpenVrAsync("display-hz", () => OpenVrSettings.ReadDisplayHz(_paths)), fps)) return true; }
                catch (IOException) { }
                await Task.Delay(500, _lifetime?.Token ?? default);
            }
            return false;
        }
        if (!await WaitForRate())
        {
            RefreshReadback.Text = $"Reconnecting SteamVR to use {fps} Hz…";
            await ReconnectFrameAsync();
            if (!await WaitForRate()) throw new IOException($"The Frame accepted {fps} Hz, but the PC stream has not confirmed it. Reconnect the Frame before playing; the requested rate is still pending.");
        }
        RefreshReadback.Text = _refreshVerifiedOnFrame ? $"Verified active display: {fps} Hz on the Frame and PC stream" : $"Verified active PC headset display: {fps} Hz";
        return _refreshVerifiedOnFrame ? $" Display verified at {fps} Hz on both the Frame and PC stream." : $" Active PC headset display verified at {fps} Hz.";
    }
}
