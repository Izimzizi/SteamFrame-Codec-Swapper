using System.Diagnostics;

namespace SteamLinkVRSettings.Frame;

public static class SteamVrRuntime
{
    public static async Task StopAsync(SteamPaths paths)
    {
        var prefix = Path.GetFullPath(Path.Combine(paths.SteamVrRoot, "bin", "win64")) + Path.DirectorySeparatorChar;
        // Stop the monitor first so it cannot relaunch the server during recovery.
        foreach (var name in new[] { "vrmonitor", "vrcompositor", "vrserver" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    if (process.HasExited) continue;
                    var executable = process.MainModule?.FileName;
                    if (executable is null || !executable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("SteamVR is running from another installation. Close it before restoring this profile.");
                    process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
                }
    }
    public static void Start(SteamPaths paths)
    {
        var file = Path.Combine(paths.SteamRoot, "steam.exe");
        if (!File.Exists(file)) throw new FileNotFoundException("Steam is unavailable. Start SteamVR manually.");
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-applaunch"); info.ArgumentList.Add("250820");
        using var process = Process.Start(info);
    }
}
