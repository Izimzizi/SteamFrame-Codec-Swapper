using Microsoft.UI.Xaml;
using SteamLinkVRSettings.Frame;
using SteamLinkVRSettings.Services;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private bool _syncingSupersample = true;

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
        if (await Task.Run(() => OpenVrSettings.IsRunning))
            await Task.Run(() => OpenVrSettings.SetPreferredRefreshRate(_paths, fps));
        else await SteamVrVideoSettings().SaveUserOverridesAsync(new() { ["preferredRefreshRate"] = (double)fps });
    }
}
