using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamLinkVRSettings.Frame;
using SteamLinkVRSettings.Services;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private readonly PresetStore _presetStore = new();
    private bool _syncingPresets;
    private TuningProfile? _pendingPreset;
    private TuningProfile? _previousProfile;
    private bool _presetRestartNeeded;
    private SettingsService DriverSettings => new() { UserConfigPath = _paths.Config, DefaultsPath = _paths.Defaults };
    private StockProfiles Profiles => new(DriverSettings, SteamVrVideoSettings(), StockProfiles.DefaultPath);

    private async Task RefreshPresetsAsync(string? selectName = null)
    {
        _syncingPresets = true;
        try
        {
            var presets = BuiltInPresets.Create().ToList();
            string? loadError = null;
            try { presets.AddRange(await _presetStore.ReadAsync()); }
            catch (Exception error) { loadError = error.Message; }
            PresetSelection.Items.Clear();
            foreach (var preset in presets)
            {
                var item = new ComboBoxItem { Content = preset.IsBuiltIn ? preset.Name + " (built-in)" : preset.Name, Tag = preset };
                PresetSelection.Items.Add(item);
                if (!preset.IsBuiltIn && selectName != null && preset.Name.Equals(selectName, StringComparison.OrdinalIgnoreCase)) PresetSelection.SelectedItem = item;
            }
            PresetSummary.Text = loadError == null ? "Choose a built-in or saved setup. Every setting can be edited before applying."
                : "Built-in presets are available. Saved presets could not be loaded: " + loadError;
        }
        catch (Exception error) { PresetSummary.Text = "Presets could not be loaded: " + error.Message; }
        finally { _syncingPresets = false; ReloadPresetButton.Visibility = PresetSelection.SelectedItem == null ? Visibility.Collapsed : Visibility.Visible; }
    }
    private async void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_actionPending) return;
        var name = new TextBox { Header = "Preset name", PlaceholderText = "For example: AV1 at 90 Hz", MaxLength = 80 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Save complete preset", PrimaryButtonText = "Save preset", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel { Spacing = 12, Children = { name, new TextBlock { Text = "Includes codec, bitrate, refresh, picture quality, encoder and additional driver settings. A matching name replaces your saved preset. Built-in presets stay available unchanged.", TextWrapping = TextWrapping.Wrap, MaxWidth = 400 } } }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await ActionAsync(async () =>
        {
            var profile = await CaptureEditorProfileAsync();
            await _presetStore.SaveAsync(name.Text, profile); await RefreshPresetsAsync(name.Text.Trim());
            return $"Preset “{name.Text.Trim()}” saved.";
        });
    }
    private async void PresetSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPresets || PresetSelection.SelectedItem is not ComboBoxItem { Tag: NamedPreset preset }) return;
        ReloadPresetButton.Visibility = Visibility.Visible;
        await ActionAsync(async () => { await LoadProfileIntoEditorAsync(preset.Profile); PresetSummary.Text = PresetLoadedText(preset); return $"Preset “{preset.Name}” loaded; the running stream has not changed."; });
    }
    private async void ReloadPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetSelection.SelectedItem is not ComboBoxItem { Tag: NamedPreset preset }) return;
        await ActionAsync(async () => { await LoadProfileIntoEditorAsync(preset.Profile); PresetSummary.Text = PresetLoadedText(preset); return $"Preset “{preset.Name}” reloaded into the editor."; });
    }
    private static string PresetLoadedText(NamedPreset preset) => (preset.Description is null ? "" : preset.Description + "\n") + $"“{preset.Name}” loaded. Edit Stream settings or Advanced, then use Apply stream settings to apply the complete preset.";
    private async Task<TuningProfile> CaptureEditorProfileAsync()
    {
        if (!double.IsFinite(BitrateInput.Value) || BitrateInput.Value % 1 != 0) throw new ArgumentException("Use a whole-number bitrate target before saving a preset.");
        var saved = await Profiles.CaptureAsync(_preferences, _patch?.IsPatched == true);
        var driver = new Dictionary<string, JsonElement>(saved.Driver);
        if (_pendingPreset != null) driver = new(_pendingPreset.Driver);
        void Set(string key, object value) => driver[key] = JsonSerializer.SerializeToElement(value);
        var paired = SelectedCodec != StreamCodec.Hevc;
        Set("targetBandwidth", BitrateInput.Value); Set("automaticBandwidth", !paired && AutomaticInput.IsOn);
        if (!double.IsFinite(StartingBandwidthInput.Value) || StartingBandwidthInput.Value % 1 != 0) throw new ArgumentException("Use a whole-number automatic starting bandwidth before saving a preset.");
        Set("recommendedBandwidthMbit", (int)StartingBandwidthInput.Value);
        Set("streamFormatWidth", paired ? SelectedCodec == StreamCodec.H264 ? 1024 : 1536 : (int)StreamWidthInput.Value);
        Set("automaticStreamFormatWidth", !paired && AutomaticWidthInput.IsOn); Set("enableAmfEncoder", !paired && AmfInput.IsOn); Set("allowMultipleLinks", !paired && MultipleLinksInput.IsOn);
        foreach (var definition in AdvancedDriverSettings.Definitions)
        {
            var row = _driverRows[definition.Key];
            if (row.Override.IsChecked == true) Set(definition.Key, definition.Kind == DriverValueKind.Boolean ? (object)row.Toggle!.IsOn : row.Number!.Value);
            else driver.Remove(definition.Key);
        }
        var video = new Dictionary<string, JsonElement>(_pendingPreset?.Video ?? saved.Video) { [OpenVrSettings.SupersampleFilteringKey] = JsonSerializer.SerializeToElement(SupersampleFilteringInput.IsOn) };
        var fps = LegacyRefreshHz;
        video["preferredRefreshRate"] = JsonSerializer.SerializeToElement(fps);
        var profile = saved with
        {
            Driver = driver, Video = video, BitratePatch = _pendingPreset?.BitratePatch ?? saved.BitratePatch, Codec = SelectedCodec, TargetMbps = checked((int)BitrateInput.Value), Fps = fps,
            QualityEnabled = QualityInput.IsOn, GazeOffset = checked((int)GazeInput.Value), PeripheralOffset = checked((int)PeripheralInput.Value), Reapply = ReapplyInput.IsChecked == true,
            RateControl = (EncoderRateControl)RateControlInput.SelectedIndex, MultiPass = (EncoderMultiPass)MultiPassInput.SelectedIndex, Advanced = ReadAdvancedEncoderInputs()
        };
        StockProfiles.Validate(profile); return profile;
    }
    private async Task LoadProfileIntoEditorAsync(TuningProfile profile)
    {
        StockProfiles.Validate(profile);
        var values = await DriverSettings.LoadDefaultsAsync();
        var videoValues = await SteamVrVideoSettings().LoadDefaultsAsync();
        foreach (var value in StockProfiles.Deserialize(profile.Video)) videoValues[value.Key] = value.Value;
        foreach (var value in StockProfiles.Deserialize(profile.Driver)) values[value.Key] = value.Value;
        _syncingCodec = _syncingBandwidth = _syncingAdvanced = true;
        try
        {
            CodecSelectionInput.SelectedIndex = (int)profile.Codec;
            BitrateInput.Maximum = CodecSwitchRequest.MaximumMbpsFor(profile.Codec); BitrateInput.Value = profile.TargetMbps;
            SelectRefresh(profile.Fps);
            ShowSupersampleValue(videoValues.TryGetValue(OpenVrSettings.SupersampleFilteringKey, out var filtering) && filtering is true, "Preset value");
            bool Bool(string key, bool fallback) => values.TryGetValue(key, out var value) && value is bool b ? b : fallback;
            AutomaticInput.IsOn = Bool("automaticBandwidth", true); MultipleLinksInput.IsOn = Bool("allowMultipleLinks", false); AmfInput.IsOn = Bool("enableAmfEncoder", false);
            StreamWidthInput.Value = values.TryGetValue("streamFormatWidth", out var width) ? Convert.ToDouble(width) : 1536;
            StartingBandwidthInput.Value = values.TryGetValue("recommendedBandwidthMbit", out var starting) && starting is not null ? Math.Clamp(Convert.ToDouble(starting), 25, 500) : 200;
            AutomaticWidthInput.IsOn = Bool("automaticStreamFormatWidth", true);
            QualityInput.IsOn = profile.QualityEnabled; GazeInput.Value = profile.GazeOffset; PeripheralInput.Value = profile.PeripheralOffset;
            ReapplyInput.IsChecked = profile.Reapply; RateControlInput.SelectedIndex = (int)profile.RateControl; MultiPassInput.SelectedIndex = (int)profile.MultiPass;
            SyncAdvancedEncoder(profile.Advanced);
            foreach (var definition in AdvancedDriverSettings.Definitions)
            {
                var row = _driverRows[definition.Key]; row.Override.IsChecked = profile.Driver.ContainsKey(definition.Key); row.Dirty = true; row.Revision++;
                if (values.TryGetValue(definition.Key, out var value))
                { if (value is bool b) row.Toggle!.IsOn = b; else if (value != null) row.Number!.Value = Convert.ToDouble(value); }
            }
        }
        finally { _syncingCodec = _syncingBandwidth = _syncingAdvanced = false; }
        _pendingPreset = profile; _codecEdited = _bandwidthEdited = _encoderAdvancedEdited = _linkEdited = _policyEdited = true; _basicEditRevision++; _linkEditRevision++;
        RefreshCodecButtons(); UpdateStreamEditStatus();
    }
    private async Task ApplyPendingPresetAsync(bool paired)
    {
        if (_pendingPreset is not { } profile) return;
        // Codec bootstrap owns the packed image and encoder when paired.
        // Preserve the saved HEVC requests for returning to HEVC, without installing its hooks.
        if (!paired)
        {
            _preferences.RateControl = (EncoderRateControl)RateControlInput.SelectedIndex; _preferences.MultiPass = (EncoderMultiPass)MultiPassInput.SelectedIndex;
            _preferences.AdvancedEncoding = ReadAdvancedEncoderInputs();
            await ApplyAdvancedEncoderAsync(_preferences.AdvancedEncoding, await Task.Run(() => OpenVrSettings.IsRunning));
        }
        if (!paired && profile.Video.TryGetValue("preferredRefreshRate", out var refresh))
        {
            await ApplyHevcRefreshAsync(checked((int)refresh.GetDouble()));
        }
        else if (!paired)
        {
            if (await Task.Run(() => OpenVrSettings.IsRunning)) await Task.Run(() => OpenVrSettings.RemovePreferredRefreshRate(_paths));
            else await SteamVrVideoSettings().SaveUserOverridesAsync([], ["preferredRefreshRate"]);
        }
        _preferences.RateControl = (EncoderRateControl)RateControlInput.SelectedIndex; _preferences.MultiPass = (EncoderMultiPass)MultiPassInput.SelectedIndex; _preferences.AdvancedEncoding = ReadAdvancedEncoderInputs();
        _preferences.QualityEnabled = QualityInput.IsOn; _preferences.GazeOffset = (int)GazeInput.Value; _preferences.PeripheralOffset = (int)PeripheralInput.Value; _preferences.ReapplyAfterRestart = ReapplyInput.IsChecked == true;
        await _preferences.SaveAsync();
        // A saved asyncSend value does not switch the connection's existing FEC
        // worker. On the verified driver, finish that change before calling a
        // tested preset applied. Other builds retain their existing reconnect guidance.
        if (!paired && profile.Codec == StreamCodec.Hevc && profile.Driver.TryGetValue("asyncSend", out var sender) &&
            await Task.Run(() => OpenVrSettings.IsRunning))
        {
            var installed = await _patcher.InspectAsync(_paths.Driver);
            if (installed.Sha256 is BitratePatcher.SupportedCurrentOriginalSha256 or BitratePatcher.SupportedCurrentPatchedSha256)
            {
                var current = await _probe.GetSnapshotAsync();
                if (!current.Installed) current = await _probe.StartAsync();
                if (await _probe.ReadActiveSendModeAsync() != sender.GetBoolean())
                {
                    ShowStatus("Reconnecting SteamVR to activate this preset's sender mode…", false);
                    await ReconnectFrameAsync();
                    if (await _probe.ReadActiveSendModeAsync() != sender.GetBoolean())
                        throw new IOException("The preset's sender mode did not activate after reconnecting. Check the Frame connection before playing.");
                }
            }
        }
        _pendingPreset = null; _encoderAdvancedEdited = _policyEdited = false;
    }

    private async Task PreparePendingPresetAsync()
    {
        if (_pendingPreset is not { } selected) return;
        // Include any edits made after loading the preset, rather than replaying stale control values.
        _pendingPreset = await CaptureEditorProfileAsync();
        var patch = await _patcher.InspectAsync(_paths.Driver);
        if (selected.BitratePatch != patch.IsPatched)
        {
            if (!patch.IsSupported || patch.BackupState == BitrateBackupState.Mismatch || patch.IsPatched && patch.BackupState != BitrateBackupState.VerifiedOriginal)
                throw new InvalidOperationException(patch.PresetFailureMessage);
            _presetRestartNeeded = await Task.Run(() => OpenVrSettings.IsRunning);
            var owner = await _codecController.ReadStatusAsync();
            if (owner.Live) await EndCodecSessionAsync();
            await SteamVrRuntime.StopAsync(_paths);
            _patch = selected.BitratePatch ? await _patcher.ApplyAsync(_paths.Driver) : await _patcher.RestoreAsync(_paths.Driver);
            BindPatch();
        }
        var profile = _pendingPreset;
        var extras = StockProfiles.Deserialize(profile.Driver).Where(x => !SettingOwnership.StreamKeys.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        var removes = SettingOwnership.TuningKeys.Except(profile.Driver.Keys).Except(SettingOwnership.StreamKeys).ToList();
        await ValidateWidthBoundsAsync(extras, removes);
        if (await Task.Run(() => OpenVrSettings.IsRunning)) { await Task.Run(() => OpenVrSettings.RemoveKeys(_paths, removes)); await Task.Run(() => OpenVrSettings.Apply(_paths, extras)); }
        else await DriverSettings.SaveUserOverridesAsync(extras, removes);
    }
}
