namespace CodecSwapper;

/// <summary>Where the tray keeps its own files. It does not read or write the full app's SteamLinkVRSettings folder.</summary>
public static class AppData
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodecSwapper");
    public static string SettingsPath => Path.Combine(Root, "settings.json");
    public static string HudPath => Path.Combine(Root, "hud.json");
    public static string HevcOptionsPath => Path.Combine(Root, "hevc-options.txt");
    public static string FoveationFile => Path.Combine(Root, "foveation.txt");
    public static string EncodeTimingPath => Path.Combine(Root, "encode-timing.txt");
    public static string HudLogPath => Path.Combine(Root, "hud.log");
    public static string SessionDirectory => Path.Combine(Root, "CodecSession");
    public static string KnownHosts => Path.Combine(Root, "known_hosts");
    public static string TrayKey => Path.Combine(Root, "frame_ed25519");
    public static string DiagnosticsDirectory => Path.Combine(Root, "Diagnostics");
    public static string LogsDirectory => Path.Combine(Root, "Logs");

    public static bool IsInside(string path) =>
        Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
