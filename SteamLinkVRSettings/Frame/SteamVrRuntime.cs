using System.Diagnostics;

namespace SteamLinkVRSettings.Frame;

public static class SteamVrRuntime
{
    public static async Task StartOrRestartForReconnectAsync(SteamPaths paths, int expectedProcessId)
    {
        if (expectedProcessId > 0)
        {
            await RestartForEncoderAsync(paths, expectedProcessId);
            return;
        }
        // A stopped runtime needs a launch, not an unguarded process kill.
        foreach (var name in new[] { "vrmonitor", "vrcompositor", "vrserver" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length != 0)
                    throw new InvalidOperationException("SteamVR is starting or stopping. Wait for it to settle, then try Reconnect Frame again.");
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        Start(paths);
    }

    public static async Task RestartForEncoderAsync(SteamPaths paths, int expectedProcessId)
    {
        // Check launch and process identity before stopping anything. A codec switch
        // or a user restart may have replaced the session since the initial probe.
        if (!File.Exists(Path.Combine(paths.SteamRoot, "steam.exe")))
            throw new FileNotFoundException("Steam is unavailable. Restart SteamVR manually, then select Start monitoring.");
        var servers = Process.GetProcessesByName("vrserver");
        try
        {
            if (servers.Length != 1 || servers[0].Id != expectedProcessId)
                throw new InvalidOperationException("The SteamVR session changed. Try Start monitoring again.");
            var prefix = Path.GetFullPath(Path.Combine(paths.SteamVrRoot, "bin", "win64")) + Path.DirectorySeparatorChar;
            foreach (var name in new[] { "vrmonitor", "vrcompositor", "vrserver" })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process)
                        if (process.MainModule?.FileName is not string executable || !executable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("SteamVR is running from another installation. Restart it manually before resuming monitoring.");
            await StopAsync(paths);
            Start(paths);
        }
        finally { foreach (var server in servers) server.Dispose(); }
    }

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
