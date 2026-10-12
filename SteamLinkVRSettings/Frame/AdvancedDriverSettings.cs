namespace SteamLinkVRSettings.Frame;

public enum DriverValueKind { Boolean, Integer, Float }
public sealed record AdvancedDriverSetting(string Key, string Label, DriverValueKind Kind, double Minimum, double Maximum,
    double Step, string Hint, bool Reconnect = false)
{
    public void Validate(object? value)
    {
        if (Kind == DriverValueKind.Boolean) { if (value is not bool) throw new ArgumentException($"{Label} requires an on/off value."); return; }
        var number = Convert.ToDouble(value);
        if (!double.IsFinite(number) || number < Minimum || number > Maximum || Kind == DriverValueKind.Integer && number != Math.Truncate(number))
            throw new ArgumentException($"{Label} must be {(Kind == DriverValueKind.Integer ? "a whole number " : "")}between {Minimum} and {Maximum}.");
        if (Key.Contains("StreamFormatWidth", StringComparison.Ordinal) && number % 32 != 0)
            throw new ArgumentException($"{Label} must be a multiple of 32 pixels.");
    }
}

public static class AdvancedDriverSettings
{
    public static readonly string[] WidthBoundKeys = ["minStreamFormatWidth", "maxStreamFormatWidth", "minNonFoveatedStreamFormatWidth", "maxNonFoveatedStreamFormatWidth"];
    public static void ValidateWidthBounds(IReadOnlyDictionary<string, object?> current, IReadOnlyDictionary<string, object?> defaults,
        IReadOnlyDictionary<string, object?> writes, IEnumerable<string> removeKeys)
    {
        var removes = removeKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var pair in new[] { ("minStreamFormatWidth", "maxStreamFormatWidth", 512d, 1344d), ("minNonFoveatedStreamFormatWidth", "maxNonFoveatedStreamFormatWidth", 768d, 1920d) })
        {
            double Value(string key, double fallback)
            {
                if (writes.TryGetValue(key, out var value)) return Convert.ToDouble(value);
                if (!removes.Contains(key) && current.TryGetValue(key, out value) && value is not null) return Convert.ToDouble(value);
                return defaults.TryGetValue(key, out value) && value is not null ? Convert.ToDouble(value) : fallback;
            }
            if (Value(pair.Item1, pair.Item3) > Value(pair.Item2, pair.Item4))
                throw new ArgumentException($"{pair.Item1} cannot exceed {pair.Item2}. Override both bounds if necessary; unset driver fallbacks are {pair.Item3:0}/{pair.Item4:0} pixels.");
        }
    }
    public static readonly AdvancedDriverSetting[] Definitions =
    [
        new("minStreamFormatWidth", "Automatic-width minimum (pixels)", DriverValueKind.Integer, 512, 4096, 32, "Used when automatic width is enabled. An unset fallback is not treated as an observed zero."),
        new("maxStreamFormatWidth", "Automatic-width maximum (pixels)", DriverValueKind.Integer, 512, 4096, 32, "Used when automatic width is enabled. Keep maximum at least the minimum."),
        new("nonFoveatedStreamFormatWidth", "Nonfoveated stream width (pixels)", DriverValueKind.Integer, 512, 4096, 32, "Used by the nonfoveated path, not a gaze-region radius.", true),
        new("minNonFoveatedStreamFormatWidth", "Nonfoveated automatic-width minimum", DriverValueKind.Integer, 512, 4096, 32, "Minimum for the nonfoveated automatic-width path; an unset fallback remains unset."),
        new("maxNonFoveatedStreamFormatWidth", "Nonfoveated automatic-width maximum", DriverValueKind.Integer, 512, 4096, 32, "Maximum for the nonfoveated automatic-width path. Keep it at least the corresponding minimum."),
        new("foveationMode", "Foveation mode (numeric)", DriverValueKind.Integer, 0, 2, 1, "Researched predicate: 0 and 2 are foveated; 1 is unfoveated. These are not labeled auto/forced. Region geometry is not edited.", true),
        new("usePool", "Encoder resource pooling", DriverValueKind.Boolean, 0, 1, 1, "Uses Valve's optional resource pool; it does not shrink the frame ring. Compare queue and encode stalls after reconnecting.", true),
        new("asyncStartEncode", "Asynchronous encoder start", DriverValueKind.Boolean, 0, 1, 1, "Changes the encoder-start scheduling path; compare encoder/queue timing after reconnecting.", true),
        new("10bit", "10-bit encoding request", DriverValueKind.Boolean, 0, 1, 1, "HEVC path only on a supported device. Read the accepted bit depth in the dashboard; the researched H.264 path rejects 10-bit input.", true),
        new("useSharpening", "Image sharpening", DriverValueKind.Boolean, 0, 1, 1, "Changes apparent crispness; it cannot restore detail lost to compression."),
        new("sharpeningStrength", "Sharpening strength", DriverValueKind.Float, 0, 1, 0.05, "Driver clamps strength to 0–1."),
        new("enableTimedRetry", "Timed video retry", DriverValueKind.Integer, 0, 1, 1, "0 disabled, 1 enabled. Watch receiver retry and decoder recovery events; disabling retry can worsen loss."),
        new("forceBaselineVideoFEC", "Force baseline video FEC", DriverValueKind.Boolean, 0, 1, 1, "Controls baseline forward-error correction; changes can trade network overhead against unrecoverable loss.", true),
        new("enableQoS", "Network QoS marking", DriverValueKind.Boolean, 0, 1, 1, "Requests packet priority marking. Benefits depend on the network's handling."),
        new("asyncSend", "Asynchronous sending", DriverValueKind.Boolean, 0, 1, 1, "Changes the sender scheduling path. Compare actual delays and recovery counts."),
        new("backoffRecoveryCoefficient", "Bandwidth backoff recovery coefficient", DriverValueKind.Float, 0.5, 10, 0.1, "Controls adaptive recovery after bandwidth backoff; monitor stability when changing it.")
    ];
}
public sealed record DriverSettingReadback(object? Value, string Message, int ErrorCode = 0)
{
    public bool Available => Value is not null;
}
