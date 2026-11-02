using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodecSwapper;

/// <summary>Where the in-VR HUD sits and which controller carries it. Stored beside the tray settings as hud.json.</summary>
public sealed class HudSettings
{
    public const float MinimumWidth = .10f, MaximumWidth = .50f, DefaultWidth = .2135f;

    /// <summary>Whether the HUD is showing right now. Not remembered: at start-up and whenever SteamVR starts it becomes <see cref="AutoShow"/>; Hide HUD and the tray menu change it for the current SteamVR session only.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Whether the HUD appears by itself when SteamVR starts.</summary>
    public bool AutoShow { get; set; } = true;
    /// <summary>The full panel with its controls appears when the laser points at the HUD, instead of when the dashboard opens.</summary>
    public bool ExpandOnHover { get; set; }
    /// <summary>The HUD stays out of sight until a small round icon on the controller, like a watch on the wrist, is pressed with the laser.</summary>
    public bool Watch { get; set; } = true;
    /// <summary>With <see cref="Watch"/>: whether the panel is open right now. Not remembered; a new SteamVR session starts with it closed.</summary>
    public bool PanelOpen { get; set; }
    /// <summary>"left" or "right".</summary>
    public string Hand { get; set; } = "left";
    /// <summary>The HUD's pose in its controller's space as a row-major 3x4; null means the default placement.</summary>
    public float[]? Placement { get; set; }
    public float WidthMeters { get; set; } = DefaultWidth;
    /// <summary>Laser coordinates arrive in GL space (origin bottom-left); turn this off if a build reports them top-left.</summary>
    public bool FlipLaserY { get; set; } = true;

    /// <summary>The placement the author chose on a headset (Reposition, then Save): a 21 cm panel lying flat above the controller and 27 cm back toward the user, clear of the controller models SteamVR draws over overlays.</summary>
    public static Pose DefaultPose { get; } = Pose.TryFromMatrix34(
        [0.9874174f, -0.017996771f, 0.1571086f, -0.004516281f, -0.15356612f, 0.12798984f, 0.97981435f, -0.011095941f, -0.0377418f, -0.9916122f, 0.123615704f, 0.27361977f], out var chosen)
        ? chosen : Pose.Rotation(0, -50, 0, new Vector3(0, .18f, -.05f));

    public const float IconWidth = .045f;

    /// <summary>The watch icon in its controller's space: a little behind the controller and above it, lying flat like the panel.</summary>
    public static Pose IconPose => DefaultPose with { T = new Vector3(0f, .02f, .10f) };

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
            loaded.Enabled = loaded.AutoShow;
            loaded.PanelOpen = false;
            loaded.PanelOpen = false;
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
