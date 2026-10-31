using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamFrameTray;

/// <summary>One line of the conversation between the tray and its HUD helper process (JSON, one object per line).</summary>
public sealed class HudMessage
{
    /// <summary>To the helper: view, settings, quit. From the helper: action, settings, log.</summary>
    [JsonPropertyName("t")] public string Type { get; set; } = "";
    public HudView? View { get; set; }
    public HudSettings? Settings { get; set; }
    /// <summary>Action kind: codec, bitrate, encoder or hevc.</summary>
    public string? Kind { get; set; }
    public bool H264 { get; set; }
    /// <summary>For the "hevc" action: whether the encoder options also apply to Default (HEVC).</summary>
    public bool Apply { get; set; }
    public int Mbps { get; set; }
    public string? RateControl { get; set; }
    public int Preset { get; set; }
    public int VbvFrames { get; set; }
    public string? Line { get; set; }

    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToLine() => JsonSerializer.Serialize(this, Options);

    public static HudMessage? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<HudMessage>(line, Options); }
        catch (JsonException) { return null; }
    }
}
