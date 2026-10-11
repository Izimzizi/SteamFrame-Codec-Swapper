namespace SteamLinkVRSettings.Tests;

public class StreamingFrameTraceTests
{
    [Fact]
    public void RecoveryTracesReportTimingsWithoutInventingDrops() => StreamingFrameTraceHarness.Run();
}
