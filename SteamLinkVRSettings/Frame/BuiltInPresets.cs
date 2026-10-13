using System.Text.Json;

namespace SteamLinkVRSettings.Frame;

/// <summary>App presets are separate from the user's catalog. Every read creates editable copies.</summary>
public static class BuiltInPresets
{
    public static IReadOnlyList<NamedPreset> Create() =>
    [
        new("72f74544-cdc3-4ff0-a03d-9421219a972e", "High quality HEVC", new()
        {
            Codec = StreamCodec.Hevc, TargetMbps = 400, Fps = 90, BitratePatch = true,
            RateControl = EncoderRateControl.Cbr, MultiPass = EncoderMultiPass.QuarterResolution,
            QualityEnabled = true, GazeOffset = -2, PeripheralOffset = -6, Reapply = true,
            // Preserve Valve's NVENC preset (P2 in the measured setup) and buffer policy.
            Advanced = new(),
            Driver = new()
            {
                ["targetBandwidth"] = Value(400d), ["recommendedBandwidthMbit"] = Value(400d),
                ["automaticBandwidth"] = Value(false), ["streamFormatWidth"] = Value(2048),
                ["automaticStreamFormatWidth"] = Value(false), ["allowMultipleLinks"] = Value(false),
                ["enableAmfEncoder"] = Value(false), ["10bit"] = Value(true),
                ["asyncSend"] = Value(true), ["enableQoS"] = Value(true),
                ["useSharpening"] = Value(true), ["sharpeningStrength"] = Value(.2d),
                ["debugRegionColoring"] = Value(false), ["showAdvancedGraphs"] = Value(true)
            },
            Video = new()
            {
                ["preferredRefreshRate"] = Value(90d), ["allowSupersampleFiltering"] = Value(false)
            }
        })
        {
            IsBuiltIn = true,
            Description = "400 Mbps target · 90 Hz · 2048-pixel width · 10-bit HEVC. CBR without padding, quarter-resolution multipass and asynchronous sending. Center −2 / outside gaze −6. Actual bitrate depends on the scene and connection."
        }
    ];

    private static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value);
}
