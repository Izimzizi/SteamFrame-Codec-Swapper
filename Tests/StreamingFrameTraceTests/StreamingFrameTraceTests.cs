namespace SteamLinkVRSettings.Tests;

public class StreamingFrameTraceTests
{
    [Fact]
    public void RecoveryTracesReportTimingsWithoutInventingDrops() => StreamingFrameTraceHarness.Run();

    [Fact]
    public void A_trace_still_being_written_does_not_replace_the_last_complete_summary()
    {
        var logs = Directory.CreateTempSubdirectory("slvr-trace-tests-").FullName;
        try
        {
            const string header = "Present ID,TX ID,WGP Release (realtime),Encode Start,Encode End,Send Start,Receive,Decode Start (remote),Decode End (remote),Submit Illumination,Predicted Display";
            var complete = Path.Combine(logs, "vrlink_aftm_2026-10-13-13-21-39.csv");
            File.WriteAllText(complete, header + "\n7,3,1791753882232345,1000,6000,6000,16000,500000,508000,28000,26000\n");
            File.SetLastWriteTimeUtc(complete, DateTime.UtcNow.AddMinutes(-2));
            var trace = new Frame.StreamingFrameTrace(logs);
            Assert.Equal(1, trace.ReadLatest()!.SentFrames);
            File.WriteAllText(Path.Combine(logs, "vrlink_aftm_2026-10-13-13-22-09.csv"), "");
            Assert.Equal("vrlink_aftm_2026-10-13-13-21-39.csv", trace.ReadLatest()!.FileName);
            File.WriteAllText(Path.Combine(logs, "vrlink_aftm_2026-10-13-13-22-10.csv"), header + "\n");
            Assert.Equal("vrlink_aftm_2026-10-13-13-21-39.csv", trace.ReadLatest()!.FileName);
        }
        finally { Directory.Delete(logs, true); }
    }
}
