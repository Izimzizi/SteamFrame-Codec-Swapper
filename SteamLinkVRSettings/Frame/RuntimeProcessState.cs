using System.ComponentModel;
using System.Diagnostics;

namespace SteamLinkVRSettings.Frame;

/// <summary>Process enumeration can briefly retain an exited runtime during shutdown.</summary>
internal static class RuntimeProcessState
{
    public static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return AnyRunning(processes); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static bool AnyRunning(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try { if (!process.HasExited) return true; }
            // An unreadable process is not permission to replace a loaded driver.
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            { return true; }
        }
        return false;
    }
}
