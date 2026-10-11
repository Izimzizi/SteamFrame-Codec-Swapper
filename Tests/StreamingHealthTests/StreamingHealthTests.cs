namespace SteamLinkVRSettings.Tests;

public class StreamingHealthTests
{
    [Fact]
    public void StreamingLogCollectionIsBoundedSafeAndIncremental() => StreamingHealthHarness.Run();
}
