using CommunityToolkit.WinUI.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SteamLinkVRSettings.Controls;
using SteamLinkVRSettings.Models;
using SteamLinkVRSettings.ViewModels;
using Windows.UI;
using Color = Windows.UI.Color;

namespace SteamLinkVRSettings.Views;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel _vm = new();

    public SettingsPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await _vm.LoadCommand.ExecuteAsync(null);
        BuildUI();
        LoadingOverlay.Visibility = Visibility.Collapsed;
        BindHeader();
    }

    private void BindHeader()
    {
        HmdInfoText.Text = string.IsNullOrEmpty(_vm.HmdInfo) ? "" : $"HMD: {_vm.HmdInfo}";
        GpuInfoText.Text = string.IsNullOrEmpty(_vm.GpuInfo) ? "" : $"GPU: {_vm.GpuInfo}";

        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(_vm.HasUnsavedChanges))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    SaveButton.IsEnabled = _vm.HasUnsavedChanges;
                    UnsavedBar.IsOpen = _vm.HasUnsavedChanges;
                    UnsavedBar.Message = $"You have unsaved changes ({_vm.ModifiedCount} modified from defaults)";
                });
            }
            else if (args.PropertyName == nameof(_vm.ModifiedCount))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UnsavedText.Text = $"{_vm.ModifiedCount} settings differ from defaults";
                });
            }
        };
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
            {
                var card = CreateSettingCard(setting);
                expander.Items.Add(card);
            }

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

        // Modified indicator dot
        var indicator = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            Fill = new SolidColorBrush(Color.FromArgb(255, 255, 179, 71)),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = setting.IsModified ? 1.0 : 0.0
        };

        setting.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingItem.IsModified))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    indicator.Opacity = setting.IsModified ? 1.0 : 0.0;
                });
            }
        };

        // Use a FontIcon as the HeaderIcon with the indicator dot overlayed
        var headerIcon = new FontIcon
        {
            Glyph = setting.IsModified ? "\uEA3B" : "\uE73E",
            FontSize = 10,
            Foreground = setting.IsModified
                ? new SolidColorBrush(Color.FromArgb(255, 255, 179, 71))
                : new SolidColorBrush(Colors.Transparent)
        };

        setting.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingItem.IsModified))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    headerIcon.Glyph = setting.IsModified ? "\uEA3B" : "\uE73E";
                    headerIcon.Foreground = setting.IsModified
                        ? new SolidColorBrush(Color.FromArgb(255, 255, 179, 71))
                        : new SolidColorBrush(Colors.Transparent);
                });
            }
        };

        card.HeaderIcon = headerIcon;

        // Content control
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
                toggle.Toggled += (_, _) =>
                {
                    setting.BoolValue = toggle.IsOn;
                };
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
                    TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                };
                var slider = new NoWheelSlider
                {
                    Minimum = setting.Minimum,
                    Maximum = setting.Maximum,
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

        // Reset button
        var resetBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE777", FontSize = 12 },
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = setting.IsModified ? Visibility.Visible : Visibility.Collapsed
        };
        ToolTipService.SetToolTip(resetBtn, $"Reset to default ({setting.DefaultValue})");
        resetBtn.Click += (_, _) => setting.ResetToDefaultCommand.Execute(null);
        setting.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingItem.IsModified))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    resetBtn.Visibility = setting.IsModified ? Visibility.Visible : Visibility.Collapsed;
                });
            }
        };
        contentPanel.Children.Add(resetBtn);

        card.Content = contentPanel;
        return card;
    }

    private static string BuildDescription(SettingItem setting)
    {
        var desc = setting.Description;
        if (setting.RequiresRestart)
            desc += " (requires SteamVR restart)";
        return desc;
    }

    private static string FormatSliderValue(SettingItem setting)
    {
        if (setting.StepFrequency >= 1)
            return $"{(int)setting.NumericValue} {setting.Unit}".Trim();
        return $"{setting.NumericValue:F1} {setting.Unit}".Trim();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await _vm.SaveCommand.ExecuteAsync(null);
        StatusBar.Severity = _vm.IsStatusError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        StatusBar.Message = _vm.StatusMessage;
        StatusBar.IsOpen = true;
    }

    private void ResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.ResetAllToDefaultsCommand.Execute(null);
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        await _vm.LoadCommand.ExecuteAsync(null);
        BuildUI();
        BindHeader();
        LoadingOverlay.Visibility = Visibility.Collapsed;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = "Settings reloaded from disk";
        StatusBar.IsOpen = true;
    }
}
