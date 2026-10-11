using System.Diagnostics;

namespace SteamLinkVRSettings.Tests;

public class FrameReceiverDiagnosticsTests
{
    [Fact]
    public async Task ReceiverLogCollectionIsBoundedSafeAndIncremental()
    {
        // The headset-side collector is Python; these tests execute it locally against fixture logs.
        if (!PythonAvailable()) Assert.Skip("Python 3 is not on PATH; receiver collector tests need it to run the headset script locally.");
        await FrameReceiverDiagnosticsHarness.RunAsync();
    }

    private static bool PythonAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("python", "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (process is null) return false;
            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
