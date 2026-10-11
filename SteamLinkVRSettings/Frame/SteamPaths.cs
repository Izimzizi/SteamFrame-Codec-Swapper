using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLinkVRSettings.Frame;

public sealed record SteamPaths(string SteamRoot, string SteamVrRoot)
{
    public string Config => Path.Combine(SteamRoot, "config", "steamvr.vrsettings");
    public string Defaults => Path.Combine(SteamVrRoot, "drivers", "vrlink", "resources", "settings", "default.vrsettings");
    public string Driver => Path.Combine(SteamVrRoot, "drivers", "vrlink", "bin", "win64", "driver_vrlink.dll");
    public string OpenVrApi => Path.Combine(SteamVrRoot, "bin", "win64", "openvr_api.dll");

    private static readonly object CacheGate = new();
    private static SteamPaths? _current;

    /// <summary>Cached discovery result; registry and file probes run once per app session unless refreshed.</summary>
    public static SteamPaths Current
    {
        get { lock (CacheGate) return _current ??= Discover(); }
    }

    public static SteamPaths Refresh()
    {
        var discovered = Discover();
        lock (CacheGate) return _current = discovered;
    }

    public static SteamPaths Discover()
    {
        string steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        steam = Path.GetFullPath(steam);
        var runtime = Path.Combine(steam, "steamapps", "common", "SteamVR");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath")));
            var found = doc.RootElement.GetProperty("runtime").EnumerateArray().Select(x => x.GetString()).FirstOrDefault(p => p != null && Directory.Exists(p));
            if (found != null) runtime = found;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException) { }
        if (!Directory.Exists(runtime))
        {
            var libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            try
            {
            if (File.Exists(libraries))
                foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s+\"([^\"]+)\""))
                {
                    var candidate = Path.Combine(match.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common", "SteamVR");
                    if (Directory.Exists(candidate)) { runtime = candidate; break; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return new(steam, Path.GetFullPath(runtime));
    }
}
