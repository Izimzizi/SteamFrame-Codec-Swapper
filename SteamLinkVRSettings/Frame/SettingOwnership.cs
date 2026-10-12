using SteamLinkVRSettings.Helpers;
using SteamLinkVRSettings.Models;

namespace SteamLinkVRSettings.Frame;

/// <summary>A setting has one editable home; defaults also use this catalog.</summary>
public static class SettingOwnership
{
    private static readonly Dictionary<string, SettingItem> ValueDefinitions = SettingDefinitions.BuildCategories().SelectMany(x => x.Settings).ToDictionary(x => x.Key, StringComparer.Ordinal);
    public static readonly string[] StreamKeys = ["targetBandwidth", "automaticBandwidth", "allowMultipleLinks", "enableAmfEncoder", "streamFormatWidth", "automaticStreamFormatWidth"];
    public static readonly HashSet<string> GuidedKeys = StreamKeys.Concat(AdvancedDriverSettings.Definitions.Select(x => x.Key)).ToHashSet(StringComparer.Ordinal);
    public static readonly HashSet<string> TuningKeys = SettingDefinitions.BuildCategories().SelectMany(x => x.Settings).Select(x => x.Key)
        .Concat(GuidedKeys).Append("recommendedBandwidthMbit").ToHashSet(StringComparer.Ordinal);
    public static readonly string[] VideoKeys = ["allowSupersampleFiltering", "preferredRefreshRate"];
    public static void ValidateValue(string key, object? value)
    {
        var advanced = AdvancedDriverSettings.Definitions.FirstOrDefault(x => x.Key == key);
        if (advanced != null) { advanced.Validate(value); return; }
        if (key == "allowSupersampleFiltering") { if (value is not bool) throw new IOException("Filtering needs an on/off value."); return; }
        if (key == "preferredRefreshRate") { if (value is not int and not double || Convert.ToDouble(value) is < 0 or > 240) throw new IOException("Invalid display refresh rate."); return; }
        if (!ValueDefinitions.TryGetValue(key, out var definition)) return;
        if (definition.ControlType == ControlType.Toggle) { if (value is not bool) throw new IOException($"{key} needs an on/off value."); return; }
        if (definition.ControlType == ControlType.Dropdown && definition.Options.All(x => x.Value is string))
        {
            if (value is not string text || !definition.Options.Any(x => Equals(x.Value, text))) throw new IOException($"{key} has an unsupported selection.");
            return;
        }
        if (value is not int and not double || !double.IsFinite(Convert.ToDouble(value))) throw new IOException($"{key} needs a number.");
        if (definition.ControlType is ControlType.Slider or ControlType.NumberBox && (Convert.ToDouble(value) < definition.Minimum || Convert.ToDouble(value) > definition.Maximum))
            throw new IOException($"{key} is outside its supported range.");
        if (key == "streamFormatWidth" && Convert.ToDouble(value) % 32 != 0) throw new IOException("Stream width must be a multiple of 32 pixels.");
    }
    public static object? NormalizeValue(string key, object? value)
    {
        if (value is not double number) return value;
        var advanced = AdvancedDriverSettings.Definitions.FirstOrDefault(x => x.Key == key);
        if (advanced != null)
        {
            advanced.Validate(number);
            return advanced.Kind == DriverValueKind.Integer ? (object)checked((int)number) : number;
        }
        ValueDefinitions.TryGetValue(key, out var definition);
        if (definition == null || definition.StoreAsFloat || definition.StepFrequency < 1) return number;
        if (definition.ControlType is ControlType.NumberBox or ControlType.Slider || definition.ControlType == ControlType.Dropdown && definition.Options.Any(x => x.Value is int))
        {
            if (number != Math.Truncate(number)) throw new ArgumentException($"{key} requires a whole-number value.");
            return checked((int)number);
        }
        return number;
    }
}
