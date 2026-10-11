using System.Globalization;
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

    /// <summary>Debug/developer toggles that can break streaming; saving a change requires confirmation.</summary>
    public bool IsExperimental { get; init; }

    /// <summary>
    /// Store slider values as floating point even with a whole-number step. SteamVR reads some keys
    /// (e.g. targetBandwidth) as floats; every writer must use the same JSON/OpenVR type.
    /// </summary>
    public bool StoreAsFloat { get; init; }

    /// <summary>Set when the stored value could not be interpreted; the control shows a fallback.</summary>
    public string? LoadWarning { get; set; }

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
        get => CurrentValue is null ? 0 : Convert.ToDouble(CurrentValue, CultureInfo.InvariantCulture);
        set => CurrentValue = NormalizeNumber(value);
    }

    /// <summary>Applies this setting's storage type to a number.</summary>
    public object NormalizeNumber(double value)
    {
        if (StepFrequency >= 1)
            // Box each branch separately: a plain conditional would widen the int branch to double.
            return StoreAsFloat ? (object)Math.Round(value) : (object)(int)Math.Round(value);
        return Math.Round(value, 1);
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
            if (CurrentValue is null || Options.Count == 0) return -1;
            return IndexOfOption(CurrentValue);
        }
        set
        {
            if (value >= 0 && value < Options.Count)
                CurrentValue = Options[value].Value;
        }
    }

    private int IndexOfOption(object value)
    {
        var text = Invariant(value);
        for (int i = 0; i < Options.Count; i++)
        {
            if (Invariant(Options[i].Value) == text) return i;
        }
        return -1;
    }

    private static string? Invariant(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Values outside the predefined list (hand-edited or newer SteamVR values) get their own
    /// "Custom" entry instead of being displayed as the first option.
    /// </summary>
    public void AddCustomOptionsIfNeeded()
    {
        if (ControlType != ControlType.Dropdown) return;
        foreach (var value in new[] { CurrentValue, DefaultValue })
        {
            if (value is null || IndexOfOption(value) >= 0) continue;
            var label = $"Custom ({Invariant(value)}{(string.IsNullOrEmpty(Unit) ? "" : " " + Unit)})";
            Options.Add(new OptionItem { Label = label, Value = value });
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
