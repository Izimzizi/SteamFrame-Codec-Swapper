namespace SteamLinkVRSettings.Tests;

public class PatcherTests
{
    /// <summary>
    /// Needs a verified original driver, which is never committed. Set SLVR_PATCHER_FIXTURE to the legacy
    /// original and optionally SLVR_PATCHER_CURRENT_FIXTURE to the current original.
    /// </summary>
    [Fact]
    public async Task PatchRestoreAndRefusalPaths()
    {
        var legacy = Environment.GetEnvironmentVariable("SLVR_PATCHER_FIXTURE");
        if (string.IsNullOrWhiteSpace(legacy) || !File.Exists(legacy))
            Assert.Skip("Set SLVR_PATCHER_FIXTURE to a verified original driver_vrlink.dll to run patcher tests.");
        var current = Environment.GetEnvironmentVariable("SLVR_PATCHER_CURRENT_FIXTURE");
        await PatcherHarness.RunAsync(string.IsNullOrWhiteSpace(current) ? [legacy] : [legacy, current]);
    }
}
