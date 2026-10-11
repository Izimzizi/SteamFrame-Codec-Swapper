using CommunityToolkit.WinUI.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SteamLinkVRSettings.Controls;
using SteamLinkVRSettings.Models;
using SteamLinkVRSettings.ViewModels;
using Color = Windows.UI.Color;

namespace SteamLinkVRSettings.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly Color ModifiedColor = Color.FromArgb(255, 255, 179, 71);
    private readonly SettingsViewModel _vm = new();

    public SettingsPage()
    {
        InitializeComponent();
        // Subscribe once; reloading rebuilds the controls but must not stack handlers.
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SettingsViewModel.HasUnsavedChanges) or nameof(SettingsViewModel.UnsavedCount) or nameof(SettingsViewModel.ModifiedCount))
                DispatcherQueue.TryEnqueue(UpdateChangeBanner);
        };
        StatusBar.ActionButton = null;
    }

    public bool HasUnsavedChanges => _vm.HasUnsavedChanges;

    private async void Page_Loaded(object sender, RoutedEventArgs e) => await ReloadAsync(announce: false);

    private async Task ReloadAsync(bool announce)
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        await _vm.LoadCommand.ExecuteAsync(null);
        BuildUI();
        BindHeader();
        UpdateChangeBanner();
        LoadingOverlay.Visibility = Visibility.Collapsed;
        if (_vm.ShowStatus) ShowViewModelStatus(_vm.IsStatusError ? InfoBarSeverity.Error : InfoBarSeverity.Warning);
        else if (announce) ShowStatus("Settings reloaded from disk", InfoBarSeverity.Informational);
    }

    private void BindHeader()
    {
        HmdInfoText.Text = string.IsNullOrEmpty(_vm.HmdInfo) ? "" : $"HMD: {_vm.HmdInfo}";
        GpuInfoText.Text = string.IsNullOrEmpty(_vm.GpuInfo) ? "" : $"GPU: {_vm.GpuInfo}";
    }

    private void UpdateChangeBanner()
    {
        SaveButton.IsEnabled = _vm.HasUnsavedChanges;
        UnsavedBar.IsOpen = _vm.HasUnsavedChanges;
        UnsavedBar.Message = $"{_vm.UnsavedCount} unsaved change(s).";
        UnsavedText.Text = $"{_vm.ModifiedCount} setting(s) differ from SteamVR defaults.";
    }

    private void BuildUI()
    {
        SettingsPanel.Children.Clear();

        foreach (var category in _vm.Categories)
        {
            var expander = new SettingsExpander
            {
                Header = category.Name,
                Description = category.Description,
                HeaderIcon = new FontIcon { Glyph = category.Glyph },
                IsExpanded = true,
                Margin = new Thickness(0, 0, 0, 4)
            };

            foreach (var setting in category.Settings)
                expander.Items.Add(CreateSettingCard(setting));

            SettingsPanel.Children.Add(expander);
        }
    }

    private SettingsCard CreateSettingCard(SettingItem setting)
    {
        var card = new SettingsCard
        {
            Header = setting.DisplayName,
            Description = BuildDescription(setting),
        };

        // Modified-from-default marker in the header icon slot.
        var headerIcon = new FontIcon { FontSize = 10 };
        void UpdateHeaderIcon()
        {
            headerIcon.Glyph = setting.IsModified ? "" : "";
            headerIcon.Foreground = new SolidColorBrush(setting.IsModified ? ModifiedColor : Colors.Transparent);
        }
        UpdateHeaderIcon();
        card.HeaderIcon = headerIcon;

        var contentPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        switch (setting.ControlType)
        {
            case ControlType.Toggle:
                var toggle = new ToggleSwitch
                {
                    IsOn = setting.BoolValue,
                    OnContent = "On",
                    OffContent = "Off",
                    MinWidth = 0
                };
                toggle.Toggled += (_, _) => setting.BoolValue = toggle.IsOn;
                setting.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SettingItem.BoolValue))
                        DispatcherQueue.TryEnqueue(() => toggle.IsOn = setting.BoolValue);
                };
                contentPanel.Children.Add(toggle);
                break;

            case ControlType.Slider:
                var valueText = new TextBlock
                {
                    Text = FormatSliderValue(setting),
                    MinWidth = 70,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                };
                var slider = new NoWheelSlider
                {
                    Minimum = Math.Min(setting.Minimum, setting.NumericValue),
                    Maximum = Math.Max(setting.Maximum, setting.NumericValue),
                    StepFrequency = setting.StepFrequency,
                    Value = setting.NumericValue,
                    Width = 250,
                    VerticalAlignment = VerticalAlignment.Center
                };
                slider.ValueChanged += (_, args) =>
                {
                    setting.NumericValue = args.NewValue;
                    valueText.Text = FormatSliderValue(setting);
                };
                setting.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SettingItem.NumericValue))
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            slider.Value = setting.NumericValue;
                            valueText.Text = FormatSliderValue(setting);
                        });
                    }
                };
                contentPanel.Children.Add(slider);
                contentPanel.Children.Add(valueText);
                break;

            case ControlType.Dropdown:
                var combo = new NoWheelComboBox
                {
                    MinWidth = 180,
                    VerticalAlignment = VerticalAlignment.Center
                };
                foreach (var opt in setting.Options)
                    combo.Items.Add(opt.Label);
                combo.SelectedIndex = setting.SelectedIndex;
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedIndex >= 0)
                        setting.SelectedIndex = combo.SelectedIndex;
                };
                setting.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SettingItem.SelectedIndex))
                        DispatcherQueue.TryEnqueue(() => combo.SelectedIndex = setting.SelectedIndex);
                };
                contentPanel.Children.Add(combo);
                break;

            case ControlType.NumberBox:
                var numberBox = new NoWheelNumberBox
                {
                    Minimum = setting.Minimum,
                    Maximum = setting.Maximum,
                    Value = setting.NumericValue,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    SmallChange = setting.StepFrequency,
                    Width = 150,
                    VerticalAlignment = VerticalAlignment.Center
                };
                numberBox.ValueChanged += (_, args) =>
                {
                    if (!double.IsNaN(args.NewValue))
                        setting.NumericValue = args.NewValue;
                };
                setting.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SettingItem.NumericValue))
                        DispatcherQueue.TryEnqueue(() => numberBox.Value = setting.NumericValue);
                };
                contentPanel.Children.Add(numberBox);
                break;
        }

        var resetBtn = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = setting.IsModified && setting.DefaultValue is not null ? Visibility.Visible : Visibility.Collapsed
        };
        ToolTipService.SetToolTip(resetBtn, $"Reset to SteamVR default ({FormatDefault(setting)})");
        resetBtn.Click += (_, _) => setting.ResetToDefaultCommand.Execute(null);
        contentPanel.Children.Add(resetBtn);

        setting.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingItem.IsModified))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateHeaderIcon();
                    resetBtn.Visibility = setting.IsModified && setting.DefaultValue is not null ? Visibility.Visible : Visibility.Collapsed;
                });
            }
        };

        card.Content = contentPanel;
        return card;
    }

    private static string BuildDescription(SettingItem setting)
    {
        var desc = setting.Description;
        if (setting.IsExperimental) desc = "Experimental: " + desc + ". Can disrupt streaming.";
        if (setting.RequiresRestart) desc += " (requires SteamVR restart)";
        if (setting.LoadWarning is not null) desc += "\n⚠ " + setting.LoadWarning;
        return desc;
    }

    private static string FormatDefault(SettingItem setting)
    {
        if (setting.DefaultValue is null) return "unknown";
        if (setting.ControlType == ControlType.Dropdown)
            return setting.Options.FirstOrDefault(o => Equals(Convert.ToString(o.Value, System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToString(setting.DefaultValue, System.Globalization.CultureInfo.InvariantCulture)))?.Label ?? setting.DefaultValue.ToString()!;
        if (setting.ControlType == ControlType.Toggle) return setting.DefaultValue is true ? "On" : "Off";
        return $"{setting.DefaultValue} {setting.Unit}".Trim();
    }

    private static string FormatSliderValue(SettingItem setting)
    {
        if (setting.StepFrequency >= 1)
            return $"{(int)setting.NumericValue} {setting.Unit}".Trim();
        return $"{setting.NumericValue:F1} {setting.Unit}".Trim();
    }

    /// <summary>Saves, asking for confirmation first when experimental settings changed. Returns true when saved.</summary>
    public async Task<bool> SaveAsync()
    {
        var experimental = _vm.ChangedExperimentalSettings;
        if (experimental.Count > 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Save experimental settings?",
                Content = "These developer options can disrupt or break streaming:\n\n• " +
                    string.Join("\n• ", experimental.Select(s => s.DisplayName)) +
                    "\n\nYou can reset them to SteamVR defaults later.",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        }

        await _vm.SaveCommand.ExecuteAsync(null);
        ShowViewModelStatus(_vm.IsStatusError ? InfoBarSeverity.Error : InfoBarSeverity.Success);
        return !_vm.IsStatusError;
    }

    private void ShowViewModelStatus(InfoBarSeverity severity)
    {
        ShowStatus(_vm.StatusMessage, severity);
        if (_vm.NeedsElevation && !Elevation.IsElevated)
            StatusBar.ActionButton = Elevation.CreateRestartButton(XamlRoot);
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.ActionButton = null;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private void ResetAllButton_Click(object sender, RoutedEventArgs e) => _vm.ResetAllToDefaultsCommand.Execute(null);

    private async void ReloadButton_Click(object sender, RoutedEventArgs e) => await ReloadAsync(announce: true);
}
