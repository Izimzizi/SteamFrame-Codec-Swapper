using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLinkVRSettings.Helpers;
using SteamLinkVRSettings.Models;
using SteamLinkVRSettings.Services;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.ViewModels;

/// <summary>Live SteamVR access, injectable so the view model can be tested without a running runtime.</summary>
public interface ILiveSettings
{
    bool IsRunning { get; }
    Task ApplyAsync(IReadOnlyDictionary<string, object?> values, IReadOnlyCollection<string> removeKeys);
}

public sealed class OpenVrLiveSettings : ILiveSettings
{
    public bool IsRunning => OpenVrSettings.IsRunning;

    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, IReadOnlyCollection<string> removeKeys) => Task.Run(() =>
    {
        var paths = SteamPaths.Current;
        if (values.Count > 0) OpenVrSettings.Apply(paths, values);
        if (removeKeys.Count > 0) OpenVrSettings.RemoveKeys(paths, removeKeys);
    });
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _service;
    private readonly ILiveSettings _live;
    private Dictionary<string, object?> _loadedOverrides = [];
    // Converted value each control showed after the last load/save; "unsaved" means differing from this.
    private readonly Dictionary<string, object?> _baseline = [];

    public SettingsViewModel() : this(new SettingsService(), new OpenVrLiveSettings()) { }

    public SettingsViewModel(SettingsService service, ILiveSettings live)
    {
        _service = service;
        _live = live;
    }

    [ObservableProperty]
    private ObservableCollection<SettingCategory> _categories = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _showStatus;

    [ObservableProperty]
    private bool _isStatusError;

    /// <summary>True when the last failure was an access-denied error that elevation could resolve.</summary>
    [ObservableProperty]
    private bool _needsElevation;

    [ObservableProperty]
    private string _hmdInfo = "";

    [ObservableProperty]
    private string _gpuInfo = "";

    public int ModifiedCount => AllSettings.Count(s => s.IsModified);

    public int UnsavedCount => AllSettings.Count(WasChanged);

    /// <summary>Experimental settings whose value differs from the saved value.</summary>
    public IReadOnlyList<SettingItem> ChangedExperimentalSettings => AllSettings.Where(s => s.IsExperimental && WasChanged(s)).ToList();

    private IEnumerable<SettingItem> AllSettings =>
        Categories.SelectMany(c => c.Settings);

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = "";
        ShowStatus = false;
        IsStatusError = false;
        NeedsElevation = false;
        var warnings = new List<string>();

        try
        {
            Dictionary<string, object?> defaults;
            try { defaults = await _service.LoadDefaultsAsync(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                defaults = [];
                warnings.Add($"SteamVR defaults could not be read ({ex.Message}); reset buttons are unavailable.");
            }
            if (defaults.Count == 0 && warnings.Count == 0 && !File.Exists(_service.DefaultsPath))
                warnings.Add("SteamVR's default settings file was not found; reset buttons are unavailable.");

            _loadedOverrides = await _service.LoadUserOverridesAsync();
            var merged = new Dictionary<string, object?>(defaults);
            foreach (var kvp in _loadedOverrides)
                merged[kvp.Key] = kvp.Value;

            var categories = SettingDefinitions.BuildCategories();
            var unreadable = new List<string>();
            _baseline.Clear();

            foreach (var setting in categories.SelectMany(c => c.Settings))
            {
                // Each setting converts independently: one unexpected value never blocks the page.
                if (defaults.TryGetValue(setting.Key, out var def) && TryConvert(def, setting, out var convertedDefault))
                    setting.DefaultValue = convertedDefault;

                if (merged.TryGetValue(setting.Key, out var val))
                {
                    if (TryConvert(val, setting, out var converted)) setting.CurrentValue = converted;
                    else
                    {
                        setting.CurrentValue = setting.DefaultValue;
                        setting.LoadWarning = $"Stored value {FormatRaw(val)} could not be read; showing the default. Saving a change replaces it.";
                        unreadable.Add(setting.DisplayName);
                    }
                }
                else setting.CurrentValue = setting.DefaultValue;

                setting.AddCustomOptionsIfNeeded();
                _baseline[setting.Key] = setting.CurrentValue;

                setting.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(SettingItem.CurrentValue) or nameof(SettingItem.IsModified))
                        RefreshChangeState();
                };
            }

            if (unreadable.Count > 0)
                warnings.Add($"{unreadable.Count} setting(s) had unexpected values and show defaults: {string.Join(", ", unreadable)}.");

            Categories = new ObservableCollection<SettingCategory>(categories);
            RefreshChangeState();
            LoadDeviceInfo();

            if (warnings.Count > 0)
            {
                StatusMessage = string.Join(" ", warnings);
                ShowStatus = true;
                IsStatusError = false;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load: {ex.Message}";
            ShowStatus = true;
            IsStatusError = true;
            NeedsElevation = ex is UnauthorizedAccessException;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        NeedsElevation = false;
        try
        {
            var toSave = new Dictionary<string, object?>();
            var toRemove = new List<string>();

            foreach (var setting in AllSettings.Where(WasChanged))
            {
                // A value equal to Valve's default is stored by removing the override, so future
                // default changes from SteamVR updates still apply.
                if (setting.DefaultValue is not null && setting.CurrentValue is not null && setting.CurrentValue.Equals(setting.DefaultValue))
                {
                    if (_loadedOverrides.ContainsKey(setting.Key)) toRemove.Add(setting.Key);
                }
                else if (setting.CurrentValue is not null)
                {
                    toSave[setting.Key] = setting.CurrentValue;
                }
            }

            var live = _live.IsRunning;
            if (live) await _live.ApplyAsync(toSave, toRemove);
            else await _service.SaveUserOverridesAsync(toSave, toRemove);

            foreach (var pair in toSave) _loadedOverrides[pair.Key] = pair.Value;
            foreach (var key in toRemove) _loadedOverrides.Remove(key);
            foreach (var setting in AllSettings)
            {
                _baseline[setting.Key] = setting.CurrentValue;
                setting.LoadWarning = null;
            }

            RefreshChangeState();
            StatusMessage = live ? "Settings accepted by running SteamVR. Options marked for restart take effect next session." : "Settings saved for the next SteamVR session.";
            ShowStatus = true;
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
            ShowStatus = true;
            IsStatusError = true;
            NeedsElevation = ex is UnauthorizedAccessException;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ResetAllToDefaults()
    {
        foreach (var setting in AllSettings.Where(s => s.DefaultValue is not null))
        {
            setting.ResetToDefaultCommand.Execute(null);
        }
    }

    private void RefreshChangeState()
    {
        HasUnsavedChanges = AllSettings.Any(WasChanged);
        OnPropertyChanged(nameof(ModifiedCount));
        OnPropertyChanged(nameof(UnsavedCount));
    }

    private bool WasChanged(SettingItem setting)
    {
        _baseline.TryGetValue(setting.Key, out var saved);
        if (setting.CurrentValue is null && saved is null) return false;
        if (setting.CurrentValue is null || saved is null) return true;
        return !setting.CurrentValue.Equals(saved);
    }

    internal static bool TryConvert(object? value, SettingItem setting, out object? converted)
    {
        try
        {
            converted = ConvertValue(value, setting);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            converted = null;
            return false;
        }
    }

    private static object? ConvertValue(object? value, SettingItem setting)
    {
        if (value is null) return null;
        if (value is System.Text.Json.Nodes.JsonNode) throw new InvalidCastException("Structured values are not editable here.");

        return setting.ControlType switch
        {
            ControlType.Toggle => value is string text ? bool.Parse(text) : Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            ControlType.Slider or ControlType.NumberBox => setting.NormalizeNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            ControlType.Dropdown => value, // keep original type
            _ => value
        };
    }

    private static string FormatRaw(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        System.Text.Json.Nodes.JsonNode node => node.ToJsonString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?"
    };

    private void LoadDeviceInfo()
    {
        try
        {
            var configPath = _service.UserConfigPath;
            if (!File.Exists(configPath)) return;

            var json = File.ReadAllText(configPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip
            });

            if (doc.RootElement.TryGetProperty("LastKnown", out var lastKnown))
            {
                var mfg = lastKnown.TryGetProperty("HMDManufacturer", out var m) ? m.GetString() : "";
                var model = lastKnown.TryGetProperty("HMDModel", out var md) ? md.GetString() : "";
                HmdInfo = $"{mfg} {model}".Trim();
            }

            if (doc.RootElement.TryGetProperty("GpuSpeed", out var gpu))
            {
                GpuInfo = gpu.TryGetProperty("gpuSpeedVendor", out var v) ? v.GetString() ?? "" : "";
            }
        }
        catch
        {
            // Non-critical
        }
    }
}
