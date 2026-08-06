using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLinkVRSettings.Helpers;
using SteamLinkVRSettings.Models;
using SteamLinkVRSettings.Services;

namespace SteamLinkVRSettings.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _service = new();
    private Dictionary<string, object?> _defaults = [];
    private Dictionary<string, object?> _loadedOverrides = [];

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

    [ObservableProperty]
    private string _hmdInfo = "";

    [ObservableProperty]
    private string _gpuInfo = "";

    public int ModifiedCount => AllSettings.Count(s => s.IsModified);

    private IEnumerable<SettingItem> AllSettings =>
        Categories.SelectMany(c => c.Settings);

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = "";
        ShowStatus = false;

        try
        {
            _defaults = await _service.LoadDefaultsAsync();
            _loadedOverrides = await _service.LoadUserOverridesAsync();
            var merged = new Dictionary<string, object?>(_defaults);
            foreach (var kvp in _loadedOverrides)
                merged[kvp.Key] = kvp.Value;

            var categories = SettingDefinitions.BuildCategories();

            foreach (var category in categories)
            {
                foreach (var setting in category.Settings)
                {
                    if (_defaults.TryGetValue(setting.Key, out var def))
                        setting.DefaultValue = def;

                    if (merged.TryGetValue(setting.Key, out var val))
                        setting.CurrentValue = ConvertValue(val, setting);
                    else if (setting.DefaultValue is not null)
                        setting.CurrentValue = ConvertValue(setting.DefaultValue, setting);

                    setting.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName is nameof(SettingItem.CurrentValue) or nameof(SettingItem.IsModified))
                        {
                            HasUnsavedChanges = AllSettings.Any(s => WasChanged(s));
                            OnPropertyChanged(nameof(ModifiedCount));
                        }
                    };
                }
            }

            Categories = new ObservableCollection<SettingCategory>(categories);
            HasUnsavedChanges = false;

            LoadDeviceInfo();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load: {ex.Message}";
            ShowStatus = true;
            IsStatusError = true;
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
        try
        {
            // Collect all settings that differ from current file state
            var toSave = new Dictionary<string, object?>();

            // Start with existing overrides to preserve unknown keys
            foreach (var kvp in _loadedOverrides)
                toSave[kvp.Key] = kvp.Value;

            foreach (var setting in AllSettings)
            {
                toSave[setting.Key] = setting.CurrentValue;
            }

            await _service.SaveUserOverridesAsync(toSave);

            _loadedOverrides = await _service.LoadUserOverridesAsync();
            HasUnsavedChanges = false;
            StatusMessage = "Settings saved successfully";
            ShowStatus = true;
            IsStatusError = false;

            OnPropertyChanged(nameof(ModifiedCount));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
            ShowStatus = true;
            IsStatusError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ResetAllToDefaults()
    {
        foreach (var setting in AllSettings)
        {
            setting.ResetToDefaultCommand.Execute(null);
        }
    }

    private bool WasChanged(SettingItem setting)
    {
        // Compare against what's currently in the file (overrides + defaults)
        object? fileValue;
        if (_loadedOverrides.TryGetValue(setting.Key, out var ov))
            fileValue = ov;
        else if (_defaults.TryGetValue(setting.Key, out var dv))
            fileValue = dv;
        else
            return false;

        var converted = ConvertValue(fileValue, setting);
        if (setting.CurrentValue is null && converted is null) return false;
        if (setting.CurrentValue is null || converted is null) return true;
        return !setting.CurrentValue.Equals(converted);
    }

    private static object? ConvertValue(object? value, SettingItem setting)
    {
        if (value is null) return null;

        return setting.ControlType switch
        {
            ControlType.Toggle => Convert.ToBoolean(value),
            ControlType.Slider when setting.StepFrequency >= 1 => Convert.ToInt32(Convert.ToDouble(value)),
            ControlType.Slider => Math.Round(Convert.ToDouble(value), 1),
            ControlType.NumberBox => Convert.ToInt32(Convert.ToDouble(value)),
            ControlType.Dropdown => value, // keep original type
            _ => value
        };
    }

    private void LoadDeviceInfo()
    {
        try
        {
            var configPath = _service.UserConfigPath;
            if (!File.Exists(configPath)) return;

            var json = File.ReadAllText(configPath);
            var doc = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions
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
