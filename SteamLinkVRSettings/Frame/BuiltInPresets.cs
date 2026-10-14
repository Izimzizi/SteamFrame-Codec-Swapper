using System.Text.Json;

namespace SteamLinkVRSettings.Frame;

/// <summary>App presets are separate from the user's catalog. Every read creates editable copies.</summary>
public static class BuiltInPresets
{
    public static IReadOnlyList<NamedPreset> Create() =>
    [
        new("5aac1f2f-93c4-45f5-9324-c0103e0ea6ed", "Tuned HEVC",
            Hevc(400, EncoderRateControl.Cbr, EncoderMultiPass.QuarterResolution, qualityBias: true, asyncSend: false,
                width: 1664, gazeOffset: -20, peripheralOffset: -12))
        {
            IsBuiltIn = true,
            Description = "Our current tuned setup: 400 Mbps target · 90 Hz · 1664-pixel width · 10-bit HEVC. CBR without padding, quarter-resolution multipass and synchronous sending. Center −20 / outside gaze −12 favor compression quality at a lower stream width. Actual bitrate varies; sustained 350–400 Mbps has not been verified."
        },
        new("72f74544-cdc3-4ff0-a03d-9421219a972e", "High quality HEVC",
            Hevc(400, EncoderRateControl.Cbr, EncoderMultiPass.QuarterResolution, qualityBias: true, asyncSend: true))
        {
            IsBuiltIn = true,
            Description = "400 Mbps target · 90 Hz · 2048-pixel width · 10-bit HEVC. CBR without padding, quarter-resolution multipass and asynchronous sending. Center −2 / outside gaze −6. Actual bitrate depends on the scene and connection."
        },
        new("85a33a87-ace1-4a72-9c0e-baaa03287e5a", "Smooth HEVC",
            Hevc(300, EncoderRateControl.Vbr, EncoderMultiPass.SinglePass, qualityBias: false, asyncSend: false))
        {
            IsBuiltIn = true,
            Description = "300 Mbps target · 90 Hz · 2048-pixel width · 10-bit HEVC. Single-pass VBR, synchronous sending and Valve's original gaze quality. Based on our smooth baseline, with less encoding work than multipass. Actual bitrate and smoothness depend on the game and connection."
        },
        new("437377a1-52fd-45e8-89c4-4537572c3a50", "Full-resolution HEVC — Experimental",
            Hevc(400, EncoderRateControl.Cbr, EncoderMultiPass.FullResolution, qualityBias: true, asyncSend: true))
        {
            IsBuiltIn = true,
            Description = "400 Mbps target · 90 Hz · 2048-pixel width · 10-bit HEVC. High quality HEVC with full-resolution multipass analysis. Extra encoding work can worsen frame timing; a quality or bitrate improvement has not been verified. Compare against High quality HEVC."
        }
    ];

    // Allocate every profile and its dictionaries independently so editor changes
    // never leak into another built-in or the next catalog load.
    private static TuningProfile Hevc(int target, EncoderRateControl rateControl, EncoderMultiPass multiPass,
        bool qualityBias, bool asyncSend, int width = 2048, int gazeOffset = -2, int peripheralOffset = -6) => new()
        {
            Codec = StreamCodec.Hevc, TargetMbps = target, Fps = 90, BitratePatch = true,
            RateControl = rateControl, MultiPass = multiPass,
            QualityEnabled = qualityBias, GazeOffset = qualityBias ? gazeOffset : 0, PeripheralOffset = qualityBias ? peripheralOffset : 0, Reapply = true,
            // Preserve Valve's NVENC preset (P2 in the measured setup) and buffer policy.
            Advanced = new(),
            Driver = new()
            {
                ["targetBandwidth"] = Value((double)target), ["recommendedBandwidthMbit"] = Value((double)target),
                ["automaticBandwidth"] = Value(false), ["streamFormatWidth"] = Value(width),
                ["automaticStreamFormatWidth"] = Value(false), ["allowMultipleLinks"] = Value(false),
                ["enableAmfEncoder"] = Value(false), ["10bit"] = Value(true),
                ["asyncSend"] = Value(asyncSend), ["enableQoS"] = Value(true),
                ["useSharpening"] = Value(true), ["sharpeningStrength"] = Value(.2d),
                ["debugRegionColoring"] = Value(false), ["showAdvancedGraphs"] = Value(true)
            },
            Video = new()
            {
                ["preferredRefreshRate"] = Value(90d), ["allowSupersampleFiltering"] = Value(false)
            }
        };

    private static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value);
}
