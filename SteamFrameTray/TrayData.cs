namespace SteamFrameTray;

/// <summary>Where the tray keeps its own files. It does not read or write the full app's SteamLinkVRSettings folder.</summary>
public static class TrayData
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamFrameTray");
    public static string SettingsPath => Path.Combine(Root, "settings.json");
    public static string SessionDirectory => Path.Combine(Root, "CodecSession");
    public static string KnownHosts => Path.Combine(Root, "known_hosts");
    public static string TrayKey => Path.Combine(Root, "frame_ed25519");
    public static string DiagnosticsDirectory => Path.Combine(Root, "Diagnostics");
    public static string LogsDirectory => Path.Combine(Root, "Logs");

    public static bool IsInside(string path) =>
        Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
