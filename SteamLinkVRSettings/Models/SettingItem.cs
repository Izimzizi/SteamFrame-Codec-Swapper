using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SteamLinkVRSettings.Models;

public enum ControlType
{
    Toggle,
    Slider,
    Dropdown,
    NumberBox
}

public class OptionItem
{
    public string Label { get; init; } = "";
    public object Value { get; init; } = "";

    public override string ToString() => Label;
}

public partial class SettingItem : ObservableObject
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Unit { get; init; } = "";
    public ControlType ControlType { get; init; }
    public bool RequiresRestart { get; init; }

    // Slider constraints
    public double Minimum { get; init; }
    public double Maximum { get; init; }
    public double StepFrequency { get; init; } = 1;

    // Dropdown options
    public List<OptionItem> Options { get; init; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private object? _currentValue;

    [ObservableProperty]
    private object? _defaultValue;

    public bool IsModified
    {
        get
        {
            if (CurrentValue is null && DefaultValue is null) return false;
            if (CurrentValue is null || DefaultValue is null) return true;
            return !CurrentValue.Equals(DefaultValue);
        }
    }

    // Typed accessors for XAML binding
    public double NumericValue
    {
        get => Convert.ToDouble(CurrentValue ?? 0);
        set
        {
            if (StepFrequency >= 1)
                CurrentValue = (int)Math.Round(value);
            else
                CurrentValue = Math.Round(value, 1);
        }
    }

    public bool BoolValue
    {
        get => CurrentValue is true;
        set => CurrentValue = value;
    }

    public int SelectedIndex
    {
        get
        {
            if (CurrentValue is null || Options.Count == 0) return 0;
            var str = CurrentValue.ToString();
            for (int i = 0; i < Options.Count; i++)
            {
                if (Options[i].Value.ToString() == str) return i;
            }
            return 0;
        }
        set
        {
            if (value >= 0 && value < Options.Count)
                CurrentValue = Options[value].Value;
        }
    }

    partial void OnCurrentValueChanged(object? value)
    {
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(NumericValue));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(SelectedIndex));
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        CurrentValue = DefaultValue;
    }
}

public class SettingCategory
{
    public string Name { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Description { get; init; } = "";
    public List<SettingItem> Settings { get; init; } = [];
}
