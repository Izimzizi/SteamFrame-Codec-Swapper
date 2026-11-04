using System.Diagnostics;
using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>Closes SteamVR (and with it the headset's stream) so the next benchmark starts from a clean session.</summary>
internal static class SteamVrProcess
{
    private static readonly string[] Names = ["vrmonitor", "vrdashboard", "vrserver", "vrcompositor", "vrwebhelper", "vrstartup"];

    /// <summary>Stops the SteamVR processes that run from <paramref name="steamVrRoot"/> and waits until the server is gone. Returns whether SteamVR is no longer running.</summary>
    public static async Task<bool> StopAsync(string steamVrRoot, TimeSpan timeout, CancellationToken token)
    {
        var prefix = Path.GetFullPath(steamVrRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var name in Names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path is null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        process.Kill();
                    }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                }
            }
        }
        var waited = Stopwatch.StartNew();
        while (OpenVrSettings.IsRunning && waited.Elapsed < timeout) await Task.Delay(500, token);
        return !OpenVrSettings.IsRunning;
    }
}
