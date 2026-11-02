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
    /// <summary>The HUD and the watch fade out when they are more than this many degrees away from where the headset points; 0 keeps them always visible.</summary>
    public int FadeAngle { get; set; }
    /// <summary>They also fade out beyond this distance (centimetres) from the headset; 0 means any distance.</summary>
    public int FadeDistanceCm { get; set; }
    /// <summary>"left" or "right".</summary>
    public string Hand { get; set; } = "left";
    /// <summary>The HUD's pose in its controller's space as a row-major 3x4; null means the default placement.</summary>
    public float[]? Placement { get; set; }
    public float WidthMeters { get; set; } = DefaultWidth;
    /// <summary>Laser coordinates arrive in GL space (origin bottom-left); turn this off if a build reports them top-left.</summary>
    public bool FlipLaserY { get; set; } = true;

    /// <summary>The placement the author chose on a headset (Reposition, then Save): a 21 cm panel beside the controller, clear of the controller models SteamVR draws over overlays.</summary>
    public static Pose DefaultPose { get; } = Pose.TryFromMatrix34(
        [0.04150998f, -0.72687185f, -0.6855175f, -0.12338108f, 0.037100255f, -0.68451434f, 0.72805476f, -0.10563135f, -0.9984491f, -0.055654407f, -0.0014470436f, 0.18358722f], out var chosen)
        ? chosen : Pose.Rotation(0, -50, 0, new Vector3(0, .18f, -.05f));

    public const float IconMinimumWidth = .02f, IconMaximumWidth = .10f, DefaultIconWidth = .045f;

    /// <summary>The watch icon in its controller's space: a little behind the controller and above it, lying flat like the panel.</summary>
    public static Pose DefaultIconPose { get; } = Pose.TryFromMatrix34(
        [0.04230489f, -0.38319105f, -0.92269987f, -0.022165954f, 0.09310796f, -0.9179956f, 0.3855063f, 0.0037904978f, -0.9947569f, -0.1022195f, -0.0031575633f, 0.19146925f], out var chosenIcon)
        ? chosenIcon : DefaultPose with { T = new Vector3(0f, .02f, .10f) };

    /// <summary>The watch icon's pose in its controller's space as a row-major 3x4; null means <see cref="DefaultIconPose"/>.</summary>
    public float[]? IconPlacement { get; set; }
    public float IconWidthMeters { get; set; } = DefaultIconWidth;

    [JsonIgnore] public Pose IconPose => Pose.TryFromMatrix34(IconPlacement, out var pose) ? pose : DefaultIconPose;

    public void SetIconPlacement(Pose pose, float widthMeters)
    {
        IconPlacement = pose.Orthonormal().ToMatrix34();
        IconWidthMeters = Math.Clamp(widthMeters, IconMinimumWidth, IconMaximumWidth);
    }

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
        IconPlacement = null;
        IconWidthMeters = DefaultIconWidth;
    }

    public static HudSettings Load(string path)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<HudSettings>(File.ReadAllText(path));
            if (loaded is null) return new();
            loaded.Enabled = loaded.AutoShow;
            loaded.PanelOpen = false;
            loaded.FadeAngle = Math.Clamp(loaded.FadeAngle, 0, 180);
            loaded.FadeDistanceCm = Math.Clamp(loaded.FadeDistanceCm, 0, 1000);
            loaded.Hand = loaded.IsLeft ? "left" : "right";
            loaded.IconWidthMeters = float.IsFinite(loaded.IconWidthMeters) ? Math.Clamp(loaded.IconWidthMeters, IconMinimumWidth, IconMaximumWidth) : DefaultIconWidth;
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
