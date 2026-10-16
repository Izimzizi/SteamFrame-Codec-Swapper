using Microsoft.UI.Xaml;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private async Task RefreshStockStatusAsync()
    {
        try
        {
            var profile = await Profiles.CaptureAsync(_preferences, _patch?.IsPatched == true, _codecService);
            StockStatus.Text = $"{profile.Driver.Count + profile.Video.Count} saved tuning overrides. " + (_patch?.IsPatched == true ? "HEVC bitrate override installed." : "No verified bitrate override is installed.");
            _previousProfile = await Profiles.ReadAsync();
            StockSnapshotStatus.Text = _previousProfile == null ? "No previous tuning profile saved yet." : $"Previous tuning saved {_previousProfile.SavedAt.ToLocalTime():g}. Codec: {_previousProfile.Codec}.";
            RefreshCodecButtons();
        }
        catch (Exception error) { StockStatus.Text = "Could not inspect the tuning profile: " + error.Message; }
    }
    private async void RestoreStock_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        await EnsureCodecStableAsync();
        var state = await _codecController.ReadStatusAsync();
        var driver = await _patcher.InspectAsync(_paths.Driver);
        if (driver.IsPatched && driver.BackupState != BitrateBackupState.VerifiedOriginal) throw new InvalidOperationException("The original driver backup could not be verified. Recover that backup before restoring stock.");
        // Do not touch live state unless the recovery snapshot has reached disk.
        var snapshot = await Profiles.CaptureAsync(_preferences, driver.IsPatched, state);
        var previous = await Profiles.ReadAsync();
        if (!snapshot.IsStock || previous == null) { await Profiles.SaveAsync(snapshot); _previousProfile = snapshot; }
        else _previousProfile = previous;
        // The recovery snapshot is durable. Do not re-arm the old encoder policy
        // on the next restart if a later driver/file operation cannot complete.
        _preferences.ReapplyAfterRestart = false;
        await _preferences.SaveAsync();
        _syncingBandwidth = true;
        try { ReapplyInput.IsChecked = false; }
        finally { _syncingBandwidth = false; }
        var wasRunning = await Task.Run(() => OpenVrSettings.IsRunning);
        _codecSwitching = true; MarkCodecReconnect();
        try
        {
            if (state.Live) await _codecController.CloseAsync(new Progress<CodecServiceStatus>(RenderCodecService));
            await _powerLease.RestoreAsync();
            var probe = await _probe.GetSnapshotAsync();
            if (probe.Installed && !probe.PairedCodecTrialPresent) await _probe.StopAsync();
            await SteamVrRuntime.StopAsync(_paths);
            if (driver.IsPatched) await _patcher.RestoreAsync(_paths.Driver);
            await Profiles.ResetAsync(_preferences); await _preferences.SaveAsync();
            string frameMessage;
            try { await _connection.ResetDisplayPreferenceAsync(); frameMessage = " Frame display preference reset."; }
            catch (Exception) { frameMessage = " Frame display reset is pending; connect its developer SSH and retry Back to stock."; }
            _pendingPreset = null; _codecEdited = _bandwidthEdited = _linkEdited = false; _encoderAdvancedEdited = _policyEdited = false; _configuredPid = 0;
            InitializeCodecControls(); SyncAdvancedEncoder(new());
            _syncingBandwidth = true;
            try { QualityInput.IsOn = false; ReapplyInput.IsChecked = false; RateControlInput.SelectedIndex = MultiPassInput.SelectedIndex = 0; }
            finally { _syncingBandwidth = false; }
            foreach (var row in _driverRows.Values) row.Dirty = false;
            if (wasRunning) SteamVrRuntime.Start(_paths);
            await LoadControlsAsync(); await LoadSteamVrVideoAsync();
            _patch = await _patcher.InspectAsync(_paths.Driver); BindPatch();
            _codecService = await _codecController.ReadStatusAsync(); await RefreshStockStatusAsync();
            return "Valve streaming defaults restored. Your previous tuning was saved. Reconnect the Frame when SteamVR is ready." + frameMessage;
        }
        finally { _codecSwitching = false; UpdatePowerLease(); }
    });
    private async void LoadPreviousProfile_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        var profile = await Profiles.ReadAsync() ?? throw new InvalidOperationException("No previous tuning profile is saved.");
        await LoadProfileIntoEditorAsync(profile); SectionRequested?.Invoke("stream");
        PresetSummary.Text = "Previous tuning loaded. Review it and apply when ready.";
        return "Previous tuning loaded into the editor. Apply to restore it; an experimental codec may need its password again.";
    });
}
