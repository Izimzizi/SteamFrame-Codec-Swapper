using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodecSwapper;

/// <summary>Where the in-VR HUD sits and which controller carries it. Stored beside the tray settings as hud.json.</summary>
public sealed class HudSettings
{
    public const float MinimumWidth = .10f, MaximumWidth = .50f, DefaultWidth = .30f;

    /// <summary>On unless the user turned it off (from the HUD itself or the tray menu).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>"left" or "right".</summary>
    public string Hand { get; set; } = "left";
    /// <summary>The HUD's pose in its controller's space as a row-major 3x4; null means the default placement.</summary>
    public float[]? Placement { get; set; }
    public float WidthMeters { get; set; } = DefaultWidth;
    /// <summary>Laser coordinates arrive in GL space (origin bottom-left); turn this off if a build reports them top-left.</summary>
    public bool FlipLaserY { get; set; } = true;

    /// <summary>Above the controller, tilted back toward the face of someone holding it out in front of them.</summary>
    public static Pose DefaultPose { get; } = Pose.Rotation(0, -50, 0, new Vector3(0, .07f, -.02f));

    [JsonIgnore] public bool IsLeft => !string.Equals(Hand, "right", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore] public Pose Pose => Pose.TryFromMatrix34(Placement, out var pose) ? pose : DefaultPose;

    public void SetPlacement(Pose pose, float widthMeters)
    {
        Placement = pose.Orthonormal().ToMatrix34();
        WidthMeters = Math.Clamp(widthMeters, MinimumWidth, MaximumWidth);
    }

    public void ResetPlacement()
    {
        Placement = null;
        WidthMeters = DefaultWidth;
    }

    public static HudSettings Load(string path)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<HudSettings>(File.ReadAllText(path));
            if (loaded is null) return new();
            loaded.Hand = loaded.IsLeft ? "left" : "right";
            loaded.WidthMeters = float.IsFinite(loaded.WidthMeters) ? Math.Clamp(loaded.WidthMeters, MinimumWidth, MaximumWidth) : DefaultWidth;
            return loaded;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
