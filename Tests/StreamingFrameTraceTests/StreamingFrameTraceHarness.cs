using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Tests;

// Former console harness; xUnit runs it through a [Fact] so it shows in Test Explorer and dotnet test.
internal static class StreamingFrameTraceHarness
{
    public static void Run()
    {
        const string header = "Present ID,TX ID,WGP Release (realtime),Encode Start,Encode End,Send Start,Receive,Decode Start (remote),Decode End (remote),Submit Illumination,Predicted Display";
        var csv = header + "\n" +
            "7,3,1791753882232345,1000,6000,6000,16000,500000,508000,28000,26000\n" +
            "7,-1,1791753882240678,0,0,0,0,0,0,0,34000\n" +
            "8,4,1791753882249000,2000,6000,6000,0,0,0,0,34000\n";
        var trace = StreamingFrameTrace.Read(new StringReader(csv), "test.csv");
        Check(trace.SentFrames == 2, "no-transmit rows are excluded from sent frames");
        Check(trace.NoTransmitRows == 1, "no-transmit rows retain a separate count");
        Check(trace.MissingReceiveFeedback == 1 && trace.MissingIlluminationFeedback == 1, "zero feedback stays unknown");
        Check(trace.Encode!.MaximumMs == 5, "microseconds convert to milliseconds");
        Check(trace.Transport!.Samples == 1 && trace.Transport.MaximumMs == 10, "transport uses mapped sender timestamps");
        Check(trace.Decode!.MaximumMs == 8, "decode stays in the remote clock domain");
        Check(trace.LateSubmissionFrames == 1 && trace.LateSubmission!.MaximumMs == 2, "positive late submission is measured independently");
        Check(trace.FirstFrame!.Value.ToUnixTimeMilliseconds() == 1791753882232, "frame UTC derives only from host realtime");
        var reset = StreamingFrameTrace.Read(new StringReader(header + "\n" +
            "1,0,1791753882299999,1000,3000,3000,4000,600,700,6000,8000\n" +
            "1,0,1791753882232345,1000,2000,2000,3000,600,800,7000,8000\n" +
            "1,0,1791753882232345,1000,2000,2000,3000,600,800,7000,8000\n"), "reset.csv");
        Check(reset.SentFrames == 2, "transmit IDs restarting inside one PID do not merge separate frames");
        Check(reset.FirstFrame < reset.LastFrame, "wrapped ring buffers sort chronologically");
        var invalid = StreamingFrameTrace.Read(new StringReader("future,format\n1,2"), "future.csv");
        Check(invalid.Encode is null && invalid.Message.Contains("unavailable"), "unknown layouts fail without invented timings");
        var reversed = StreamingFrameTrace.Read(new StringReader(header + "\n1,1,1791753882232345,9000,2000,9000,8000,9000,2000,1000,3000\n"), "reversed.csv");
        Check(reversed.Encode is null && reversed.Transport is null && reversed.Decode is null, "reversed timestamps do not report negative durations");
        Console.WriteLine("12 frame-trace checks passed.");
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    }
}
