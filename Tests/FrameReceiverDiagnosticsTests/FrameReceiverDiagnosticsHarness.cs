using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Tests;

// Former console harness; xUnit runs it through a [Fact] so it shows in Test Explorer and dotnet test.
internal static class FrameReceiverDiagnosticsHarness
{
    public static async Task RunAsync()
    {
        const string retry = "Sun Oct 11 2026 17:24:41.954465 [Info] - SVLFEC: substantive retry (21 packets) due to frame skip (skipped 52567, last sub 52566, group 3, expected size 213536)";
        const string backtrack = "Sun Oct 11 2026 17:24:41.995845 [Info] - Received frame outside of backtrack window.  Resetting.";
        const string unrecoverable = "Sun Oct 11 2026 17:24:41.995977 [Info] - Fault, SVLFEC Received ExperiencedAnUnrecoverableError()";
        const string late = "Sun Oct 11 2026 17:24:41.996491 [Error] - [SVLFEC] received late metadata for frame [ 52572 ]";
        const string empty = "Sun Oct 11 2026 17:24:42.009429 [Error] - SVLCodecV4L2::SubmitFrameForDecode: Called submit with an empty buffer";
        const string mismatch = "Sun Oct 11 2026 17:24:42.009765 [Warning] - SVLCodecV4L2::SubmitFrameForDecode: (idx: 52570) Buffer from the pointer != m_nBufferIdForFrameIndex. 2 vs -1";
        const string queue = "Sun Oct 11 2026 17:24:42.009765 [Error] - SVLCodecV4L2::SubmitFrameForDecode: Called submit with an empty buffer -1 [22 Invalid argument]";
        var root = Path.Combine(Path.GetTempPath(), "SteamLinkVRSettings-receiver-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int assertions = 0;
        void Check(bool condition, string description)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(description);
        }

        async Task<string?> RunPython(string script, string path, CancellationToken token)
        {
            var pathBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
            script = script.Replace("path=pathlib.Path.home()/'.steam/steam/logs/vrlink.txt'",
                $"path=pathlib.Path(base64.b64decode('{pathBase64}').decode('utf-8'))", StringComparison.Ordinal);
            using var process = new Process { StartInfo = new("python") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.ArgumentList.Add("-");
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.StandardInput.WriteAsync(script.AsMemory(), token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            var result = await output;
            var errors = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("Fixture Python script failed: " + errors);
            return result;
        }

        (string Path, FrameReceiverDiagnostics Collector) Fixture(string name, bool existing = true, string content = "")
        {
            var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "vrlink.txt");
            if (existing) File.WriteAllText(path, content, new UTF8Encoding(false));
            return (path, new(new FramePreferences(), Path.Combine(directory, "capture"),
                (script, token) => RunPython(script, path, token)));
        }

        try
        {
            var f = Fixture("append", content: retry + "\n");
            var state = await f.Collector.PollAsync();
            Check(state.Available && state.TotalEvents == 0 && !File.Exists(state.CapturePath), "Existing events must start at EOF without creating capture storage.");
            var generation = state.LogGeneration;
            File.AppendAllText(f.Path, retry[..80]);
            Check((await f.Collector.PollAsync()).TotalEvents == 0, "Incomplete bytes must not create a receiver event.");
            File.AppendAllText(f.Path, retry[80..] + " secret-token=DO_NOT_CAPTURE\r\n");
            state = await f.Collector.PollAsync();
            Check(state.RetryEvents == 1 && state.RequestedRetryPackets == 21 && state.NewEvents.Count == 1, "The complete append must record exactly one request and its packet count.");
            Check(state.NewEvents[0].TimestampFromLog && state.NewEvents[0].Timestamp.Microsecond == 465, "Receiver microsecond timestamps must survive with an explicit timezone offset.");
            Check(state.LogGeneration == generation, "Ordinary appends must not treat Linux ctime changes as log rotation.");
            state = await f.Collector.PollAsync();
            Check(state.TotalEvents == 1 && state.NewEvents.Count == 0, "Unchanged bytes must not replay events.");
            File.AppendAllText(f.Path, string.Join('\n', backtrack, unrecoverable, late, empty, mismatch) + "\n");
            state = await f.Collector.PollAsync();
            Check(state.TotalEvents == 6 && state.BacktrackWindowResets == 1 && state.UnrecoverableErrors == 1 &&
                state.LateMetadataEvents == 1 && state.EmptyDecodeBuffers == 1 && state.DecodeBufferMismatches == 1,
                "All real recovery/decoder formats must have separate counters.");
            Check(state.NewEvents.Single(e => e.Category == FrameReceiverCategory.LateMetadata).FrameIndex == 52572 &&
                state.NewEvents.Single(e => e.Category == FrameReceiverCategory.DecodeBufferMismatch).FrameIndex == 52570,
                "Frame identifiers must be retained as validated numbers.");
            File.AppendAllText(f.Path, queue + " secret=QUEUE_PRIVATE\n");
            state = await f.Collector.PollAsync();
            Check(state.DecoderQueueFailures == 1 && state.EmptyDecodeBuffers == 1 && state.NewEvents.Single().ErrorNumber == 22,
                "The longer ioctl errno format must be a distinct queue failure, not a missing/stale mapped buffer.");
            Check(state.RecentEvents.Single(e => e.Category == FrameReceiverCategory.EmptyDecodeBuffer).Message == "Decoder submission found no mapped frame buffer.",
                "Plain empty-buffer evidence must not claim a hardware decoder fault.");
            var journal = File.ReadAllText(state.CapturePath);
            Check(!journal.Contains("DO_NOT_CAPTURE") && !journal.Contains("QUEUE_PRIVATE") && !journal.Contains("Invalid argument") && !journal.Contains("secret-token") && !journal.Contains("SVLCodecV4L2") && !journal.Contains("expected size"),
                "Arbitrary raw receiver lines and sensitive suffixes must never cross into persistence.");
            foreach (var line in File.ReadLines(state.CapturePath))
            {
                using var row = JsonDocument.Parse(line);
                Check(row.RootElement.GetProperty("category").ValueKind == JsonValueKind.String, "Capture must contain typed synthesized JSONL events.");
            }

            var partial = Fixture("old-partial", content: retry[..80]);
            await partial.Collector.PollAsync(); File.AppendAllText(partial.Path, retry[80..] + "\n" + empty + "\n");
            state = await partial.Collector.PollAsync();
            Check(state.RetryEvents == 0 && state.EmptyDecodeBuffers == 1, "The completion of an old startup fragment must be discarded.");

            var missing = Fixture("missing", existing: false);
            Check(!(await missing.Collector.PollAsync()).Available, "A missing log is unavailable without throwing.");
            File.WriteAllText(missing.Path, empty + "\n");
            Check((await missing.Collector.PollAsync()).EmptyDecodeBuffers == 1, "A log first created after an unavailable poll must be read from its beginning.");
            File.Delete(missing.Path);
            Check(!(await missing.Collector.PollAsync()).Available && missing.Collector.Current.TotalEvents == 1, "Temporary missing logs must preserve cumulative session counters.");

            File.WriteAllText(f.Path, backtrack + "\n");
            state = await f.Collector.PollAsync();
            Check(state.BacktrackWindowResets == 2 && state.LogGeneration != generation, "Truncation must start a new generation without losing session history.");
            generation = state.LogGeneration;
            File.Move(f.Path, f.Path + ".old"); File.WriteAllText(f.Path, empty + "\n");
            state = await f.Collector.PollAsync();
            Check(state.EmptyDecodeBuffers == 2 && state.LogGeneration != generation, "Atomic log replacement must be recognized as a new generation.");
            generation = state.LogGeneration;
            File.WriteAllText(f.Path, new string('x', 2048) + "\n" + mismatch + "\n");
            state = await f.Collector.PollAsync();
            Check(state.DecodeBufferMismatches == 2 && state.LogGeneration != generation, "Anchor validation must catch same-inode truncation/regrowth beyond the prior length.");

            var oversized = Fixture("oversized"); await oversized.Collector.PollAsync();
            File.AppendAllText(oversized.Path, new string('x', 9000) + " secret=PRIVATE");
            Check((await oversized.Collector.PollAsync()).DiscardedLines == 1, "An oversized incomplete line must be consumed without retaining its payload.");
            File.AppendAllText(oversized.Path, retry + "\n" + empty + "\n");
            state = await oversized.Collector.PollAsync();
            Check(state.RetryEvents == 0 && state.EmptyDecodeBuffers == 1 && !File.ReadAllText(state.CapturePath).Contains("PRIVATE"),
                "Discarded long lines must stay discarded through their completion and resume at the next line.");

            var invalid = Fixture("invalid"); await invalid.Collector.PollAsync();
            File.AppendAllText(invalid.Path, retry.Replace("21 packets", "9999999 packets") + "\n" + late.Replace("52572", "9999999999") + "\n" +
                "arbitrary secret log message\n" + empty.Replace("2026", "0000") + "\n");
            state = await invalid.Collector.PollAsync();
            Check(state.TotalEvents == 1 && !state.NewEvents[0].TimestampFromLog, "Invalid numbers and unknown logs must be excluded; corrupt dates must use observation time.");

            var flood = Fixture("flood"); await flood.Collector.PollAsync();
            File.AppendAllText(flood.Path, string.Concat(Enumerable.Repeat(empty + "\n", 400)));
            state = await flood.Collector.PollAsync();
            Check(state.TotalEvents == 400 && state.NewEvents.Count == FrameReceiverDiagnostics.MaximumEventsPerPoll &&
                state.RecentEvents.Count == FrameReceiverDiagnostics.MaximumRecentEvents && state.CaptureSkippedEvents == 144,
                "A burst must preserve numeric totals while bounding returned, recent, and persisted event objects.");
            Check((await flood.Collector.PollAsync()).TotalEvents == 400, "Omitted burst objects must not be replayed as new events.");
            for (int i = 0; i < 22; i++)
            {
                File.AppendAllText(flood.Path, string.Concat(Enumerable.Repeat(empty + "\n", 256)));
                await flood.Collector.PollAsync();
            }
            var captures = Directory.GetFiles(Path.GetDirectoryName(state.CapturePath)!, "frame-receiver-health.jsonl*");
            Check(captures.Length is >= 2 and <= 3 && captures.All(p => new FileInfo(p).Length <= FrameReceiverDiagnostics.MaximumCaptureFileBytes),
                "Persistent diagnostics must rotate with bounded file sizes and retention.");

            var backlog = Fixture("backlog"); await backlog.Collector.PollAsync();
            File.AppendAllText(backlog.Path, new string('x', 2 * 1024 * 1024) + "\n" + mismatch + "\n");
            for (int i = 0; i < 5; i++) state = await backlog.Collector.PollAsync();
            Check(state.SkippedBytes > 0 && state.DecodeBufferMismatches == 1 && state.TotalEvents == 1,
                "A multi-megabyte backlog must skip old bytes and reach a new event using bounded polls.");

            var noCapture = Fixture("unavailable-capture");
            var blocker = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocker, "fixture");
            var cannotCapture = new FrameReceiverDiagnostics(new(), blocker, (script, token) => RunPython(script, noCapture.Path, token));
            await cannotCapture.PollAsync(); File.AppendAllText(noCapture.Path, empty + "\n");
            state = await cannotCapture.PollAsync();
            Check(state.EmptyDecodeBuffers == 1 && state.CaptureError == "Receiver diagnostic capture is unavailable.", "Capture failure must preserve live evidence and expose only a fixed safe error.");

            var retryFailure = Fixture("transport-failure");
            bool fail = false;
            var retryCollector = new FrameReceiverDiagnostics(new(), Path.Combine(root, "retry-capture"),
                (script, token) => fail ? Task.FromResult<string?>(null) : RunPython(script, retryFailure.Path, token));
            await retryCollector.PollAsync(); File.AppendAllText(retryFailure.Path, empty + "\n");
            fail = true;
            Check(!(await retryCollector.PollAsync()).Available && retryCollector.Current.TotalEvents == 0, "Failed SSH must not consume unseen receiver events.");
            fail = false;
            Check((await retryCollector.PollAsync()).EmptyDecodeBuffers == 1, "The next successful SSH read must collect the unconsumed event exactly once.");

            File.AppendAllText(retryFailure.Path, empty + "\n");
            await Task.WhenAll(retryCollector.PollAsync(), retryCollector.PollAsync());
            Check(retryCollector.Current.EmptyDecodeBuffers == 2, "Overlapping polls must serialize cursor commits and avoid duplicate events.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var before = retryCollector.Current;
            try { await retryCollector.PollAsync(cancelled.Token); throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
            Check(ReferenceEquals(before, retryCollector.Current), "Cancellation before polling must not replace the current snapshot.");

            var badResponse = new FrameReceiverDiagnostics(new(), Path.Combine(root, "bad-response"), (_, _) => Task.FromResult<string?>("{\"raw\":\"secret\"}"));
            state = await badResponse.PollAsync();
            Check(!state.Available && state.Message == "Receiver diagnostics returned an unsupported response." && !File.Exists(state.CapturePath),
                "Malformed response data must produce a fixed error without capturing arbitrary payloads.");

            var key = Path.Combine(root, "key with spaces"); var hosts = Path.Combine(root, "known hosts");
            File.WriteAllText(key, "unread fixture"); File.WriteAllText(hosts, "unread fixture");
            var preferences = new FramePreferences { SshHost = "10.35.78.1", SshKey = key, SshKnownHosts = hosts };
            var info = FrameReceiverDiagnostics.CreateSshStartInfo(preferences);
            var sshArguments = info.ArgumentList.ToArray();
            Check(info.FileName.EndsWith(@"\System32\OpenSSH\ssh.exe", StringComparison.OrdinalIgnoreCase) && info.CreateNoWindow && !info.UseShellExecute,
                "Production SSH must use the full system OpenSSH path with no shell or visible window.");
            Check(sshArguments.Contains("StrictHostKeyChecking=yes") && sshArguments.Contains("IdentitiesOnly=yes") && sshArguments.Contains("BatchMode=yes") &&
                sshArguments.Contains("UserKnownHostsFile=\"" + hosts.Replace('\\', '/') + "\"") && sshArguments.Contains(key) && sshArguments[^3] == "steamos@10.35.78.1" && sshArguments[^2] == "python3" && sshArguments[^1] == "-",
                "SSH must pin the trusted hosts file/key and pass only the fixed read-only command via separate arguments.");
            preferences.SshHost = "10.35.78.1; reboot";
            try { FrameReceiverDiagnostics.CreateSshStartInfo(preferences); throw new InvalidOperationException("Invalid host accepted."); }
            catch (ArgumentException) { Check(true, "Host command injection must be rejected."); }
            preferences.SshHost = "10.35.78.1"; preferences.SshKnownHosts = Path.Combine(root, "missing-hosts");
            try { FrameReceiverDiagnostics.CreateSshStartInfo(preferences); throw new InvalidOperationException("Missing trust accepted."); }
            catch (ArgumentException) { Check(true, "Missing host trust must be rejected without key/credential reads."); }

            Console.WriteLine($"Frame receiver diagnostics: {assertions} assertions passed.");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("SteamLinkVRSettings-receiver-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe fixture cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }

    }
}
