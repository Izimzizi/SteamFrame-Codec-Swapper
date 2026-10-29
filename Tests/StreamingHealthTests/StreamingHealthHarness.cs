using System.Text;
using System.Text.Json;
using SteamFrame.Core;

namespace SteamFrame.Tests;

// Former console harness; xUnit runs it through a [Fact] so it shows in Test Explorer and dotnet test.
internal static class StreamingHealthHarness
{
    public static void Run()
    {
        const string reset = "Sun Oct 11 2026 17:00:32.802 [Info] - Reset video stream because we had a stream reset request [wgpReset:1 != lastReset:0]";
        const string link = "Sun Oct 11 2026 17:02:19.921 [Info] - Bad link event: d=2574.7 ms 150.0 mbit/s -> 350.0 mbit/s, ping 3.0 ms, holdoff 10, auto 0";
        const string perf = "Sun Oct 11 2026 17:00:32.742 [Info] - [Perf warning] 128.512ms - Encode Time";
        const string timeout = "Sun Oct 11 2026 17:01:54.861 [Warning] - WaitForAcquire timed out (FAILED); rendering the next frame before the driver took the sync texture [84 messages suppressed]";
        const string reconfiguration = "Sun Oct 11 2026 17:00:32.578 [Info] - Starting NVENC reset configuration Cause Mask: 111000";
        var root = Path.Combine(Path.GetTempPath(), "SteamLinkVRSettings-health-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int assertions = 0;
        void Check(bool condition, string description)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(description);
        }
        (string Logs, string Driver, string Compositor, string Diagnostics) Fixture(string name)
        {
            var logs = Path.Combine(root, name, "logs"); Directory.CreateDirectory(logs);
            return (logs, Path.Combine(logs, "driver_vrlink.txt"), Path.Combine(logs, "vrcompositor.txt"), Path.Combine(root, name, "capture"));
        }
        try
        {
            var f = Fixture("append");
            File.WriteAllText(f.Driver, link + "\n", new UTF8Encoding(false));
            File.WriteAllText(f.Compositor, timeout + "\n", new UTF8Encoding(false));
            var health = new StreamingHealth(f.Logs, f.Diagnostics);
            Check(health.Poll().TotalEvents == 0 && !File.Exists(health.CapturePath), "Startup must not replay existing warnings or create a capture.");
            File.AppendAllText(f.Driver, reset[..80]);
            Check(health.Poll().TotalEvents == 0, "An unfinished line must not emit an event.");
            File.AppendAllText(f.Driver, reset[80..] + "\n");
            var state = health.Poll();
            Check(state.StreamResetRequests == 1 && state.RecentEvents.Single().ResetRequestId == 1 && state.RecentEvents.Single().PreviousResetId == 0,
                "Complete appended recovery request should carry validated request counters.");
            Check(state.RecentEvents.Single().TimestampFromLog && state.RecentEvents.Single().Timestamp.Second == 32,
                "Steam timestamp should be preserved separately from observation time.");
            Check(health.Poll().TotalEvents == 1, "Polling unchanged bytes must not replay an event.");

            File.AppendAllText(f.Driver, perf[..55]); health.Poll(); File.AppendAllText(f.Driver, perf[55..] + "\r\n");
            Check(health.Poll().EncoderPerformanceWarnings == 1 && health.Current.RecentEvents.Last().DurationMs == 128.512,
                "A split CRLF encoder warning should yield one correctly measured duration.");
            File.AppendAllText(f.Driver, link + " secret-launch-token=DO_NOT_CAPTURE\n" + reconfiguration + "\n");
            File.AppendAllText(f.Compositor, timeout + "\n"); state = health.Poll();
            var bad = state.RecentEvents.Single(e => e.Category == StreamingHealthCategory.NetworkBadLink);
            Check(bad.DurationMs == 2574.7 && bad.PingMs == 3 && bad.PreviousBitrateMbps == 150 && bad.NewBitrateMbps == 350,
                "Real bad-link numeric metadata should survive without copying trailing text.");
            Check(state.CompositorAcquireTimeouts == 1 && state.SuppressedCompositorMessages == 84,
                "Visible timeout and suppressed warnings must be separate counts, never a fabricated headset drop count.");
            Check(state.EncoderReconfigurations == 1 && state.RecentEvents.Last(e => e.Category == StreamingHealthCategory.EncoderReconfiguration).CauseMask == "111000",
                "Cause mask should annotate reconfiguration independently of client requests.");
            var capture = File.ReadAllText(health.CapturePath);
            Check(!capture.Contains("DO_NOT_CAPTURE") && !capture.Contains("secret-launch-token") && !capture.Contains("Bad link event:"),
                "Persisted records must contain synthesized text, not raw lines or sensitive suffixes.");
            foreach (var row in File.ReadLines(health.CapturePath))
                Check(JsonDocument.Parse(row).RootElement.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.String,
                    "Capture must be valid metadata JSONL with named categories.");

            var unfinished = Fixture("initial-partial"); File.WriteAllText(unfinished.Driver, reset[..80]);
            var partial = new StreamingHealth(unfinished.Logs, unfinished.Diagnostics);
            File.AppendAllText(unfinished.Driver, reset[80..] + "\n" + reset + "\n");
            Check(partial.Poll().StreamResetRequests == 1, "Startup in an old partial line must discard its later completion.");

            var created = Fixture("created-later"); var later = new StreamingHealth(created.Logs, created.Diagnostics);
            File.WriteAllText(created.Driver, reset + "\n");
            Check(later.Poll().StreamResetRequests == 1, "A log created after monitoring starts must capture its first new event.");
            File.WriteAllText(created.Driver, perf + "\n");
            Check(later.Poll().EncoderPerformanceWarnings == 1 && later.Current.StreamResetRequests == 1,
                "Truncating a consumed log must read the new generation without replay.");
            File.Move(created.Driver, created.Driver + ".old"); File.WriteAllText(created.Driver, link + "\n");
            Check(later.Poll().NetworkBadLinkEvents == 1, "Replacing a rotated log must detect the new file.");
            var consumed = later.Current.TotalEvents;
            File.WriteAllText(created.Driver, reconfiguration + "\n" + new string('x', 1024) + "\n");
            Check(later.Poll().TotalEvents == consumed + 1, "Truncate-and-regrow beyond the old length must be detected by the consumed-byte anchor.");

            File.AppendAllText(created.Driver, perf + new string('s', StreamingHealth.MaximumLineBytes) + " PRIVATE_CREDENTIAL\n" + reset + "\n");
            state = later.Poll();
            Check(state.DiscardedLines == 1 && state.StreamResetRequests == 2,
                "Oversized lines must be discarded as a whole and collection should recover on the next line.");
            Check(!File.ReadAllText(later.CapturePath).Contains("PRIVATE_CREDENTIAL"), "Oversized raw data must never be persisted.");
            File.Delete(created.Driver); Check(!later.Poll().DriverLogAvailable, "Missing logs should be reported without crashing.");

            var observed = DateTimeOffset.Now;
            foreach (var stage in new[] { "videonvenc::GetEncodedPacket", "videonvenc::StartEncodeData", "StartEncodeData::nvEncEncodePicture", "Submitting EOS encode" })
                Check(StreamingHealth.TryParseLine("driver_vrlink.txt", "[Perf warning] 113.411ms - " + stage, observed, out var item) && item!.DurationMs == 113.411,
                    "Known encode/retrieval stage must be classified safely: " + stage);
            Check(!StreamingHealth.TryParseLine("driver_vrlink.txt", "[Perf warning] 128.512ms - untrusted-secret-stage", observed, out _) &&
                !StreamingHealth.TryParseLine("driver_vrlink.txt", "Bad link event: d=NaN ms 150 mbit/s -> 350 mbit/s, ping 1 ms", observed, out _) &&
                !StreamingHealth.TryParseLine("vrcompositor.txt", reset, observed, out _) &&
                !StreamingHealth.TryParseLine("arbitrary-secret-source", timeout, observed, out _), "Unknown labels, sources and invalid numeric data must not enter capture.");
            Check(StreamingHealth.TryParseLine("driver_vrlink.txt", "Fri Dec 31 9999 23:59:59.999 [Info] - [Perf warning] 1ms - Encode Time", observed, out _),
                "A corrupt extreme timestamp must not crash collection.");

            var unavailable = Fixture("capture-unavailable"); File.WriteAllText(unavailable.Diagnostics, "occupied");
            var captureFailure = new StreamingHealth(unavailable.Logs, unavailable.Diagnostics);
            File.WriteAllText(unavailable.Driver, reset + "\n");
            var captureState = captureFailure.Poll();
            Check(captureState.StreamResetRequests == 1 && captureState.CaptureError == "Diagnostic capture is unavailable.",
                "Unavailable capture storage must not break log collection or expose exception/path contents.");

            var flood = Fixture("bounded"); var bounded = new StreamingHealth(flood.Logs, flood.Diagnostics);
            for (var round = 0; round < 16; round++)
            {
                File.AppendAllText(flood.Driver, string.Concat(Enumerable.Repeat(reset + "\n", 1500)));
                bounded.Poll();
            }
            Check(bounded.Current.RecentEvents.Count == StreamingHealth.MaximumRecentEvents && bounded.Current.CaptureSkippedEvents > 0,
                "Warning floods must bound recent metadata and transparently count per-poll capture omissions.");
            var files = Directory.GetFiles(flood.Diagnostics);
            Check(files.Length <= 3 && files.All(p => new FileInfo(p).Length <= StreamingHealth.MaximumCaptureFileBytes),
                "Capture retention must be bounded to current file plus two size-bounded archives.");
            File.AppendAllText(flood.Driver, new string('x', 2 * 1024 * 1024) + "\n" + perf + "\n");
            for (var i = 0; i < 5; i++) bounded.Poll();
            Check(bounded.Current.SkippedBytes > 0 && bounded.Current.EncoderPerformanceWarnings == 1,
                "Huge backlogs must expose skipped bytes and still recover to new warnings with bounded polling.");
            var rootFixture = Fixture("root-path"); var byRoot = StreamingHealth.FromSteamRoot(Path.GetDirectoryName(rootFixture.Logs)!, rootFixture.Diagnostics);
            File.WriteAllText(rootFixture.Driver, reset + "\n");
            Check(byRoot.Poll().StreamResetRequests == 1, "Steam-root factory must select the logs directory.");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { byRoot.Poll(canceled.Token); throw new Exception("Canceled poll did not cancel."); }
            catch (OperationCanceledException) { Check(true, "Cancellation before collection should preserve state."); }
            Console.WriteLine($"Streaming health: {assertions} assertions passed.");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("SteamLinkVRSettings-health-tests-", StringComparison.Ordinal))
                Directory.Delete(resolved, true);
        }

    }
}
