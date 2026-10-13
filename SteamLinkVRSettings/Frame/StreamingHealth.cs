using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteamLinkVRSettings.Frame;

public enum StreamingHealthCategory
{
    StreamResetRequest,
    NetworkBadLink,
    EncoderPerformance,
    CompositorAcquireTimeout,
    EncoderReconfiguration
}

// Only synthesized text and validated numeric metadata cross the log boundary.
// Timeouts/recovery requests are evidence of a stall, not exact headset dropped-frame counts.
public sealed record StreamingHealthEvent(
    DateTimeOffset Timestamp, DateTimeOffset ObservedAt, bool TimestampFromLog,
    string Source, StreamingHealthCategory Category, string Message,
    double? DurationMs = null, double? PingMs = null,
    double? PreviousBitrateMbps = null, double? NewBitrateMbps = null,
    long? SuppressedCount = null, long? ResetRequestId = null, long? PreviousResetId = null,
    string? CauseMask = null);

public sealed record StreamingHealthSnapshot(
    DateTimeOffset ObservedAt, long TotalEvents, long StreamResetRequests,
    long NetworkBadLinkEvents, long EncoderPerformanceWarnings,
    long CompositorAcquireTimeouts, long SuppressedCompositorMessages,
    long EncoderReconfigurations, long SkippedBytes, long DiscardedLines,
    long CaptureSkippedEvents, bool DriverLogAvailable, bool CompositorLogAvailable,
    string Summary, string CapturePath, string? CaptureError,
    IReadOnlyList<StreamingHealthEvent> RecentEvents);

/// <summary>
/// Incrementally observes new SteamVR log bytes. Existing logs start at EOF;
/// newly created/replaced/truncated logs are read from their beginning. Polls are serialized.
/// A missing log or capture failure never changes SteamVR, the headset, or settings.
/// </summary>
public sealed class StreamingHealth
{
    public const int MaximumBytesPerLogPerPoll = 256 * 1024;
    public const int MaximumLineBytes = 8192;
    public const int MaximumRecentEvents = 128;
    public const int MaximumCaptureFileBytes = 1024 * 1024;
    private const int MaximumBacklogBytes = 1024 * 1024;
    private const int MaximumCapturedEventsPerPoll = 512;
    private const string DriverName = "driver_vrlink.txt", CompositorName = "vrcompositor.txt";
    private readonly object _gate = new();
    private readonly Tail _driver, _compositor;
    private readonly Queue<StreamingHealthEvent> _recent = new();
    private long _total, _resets, _links, _encoder, _compositorTimeouts, _suppressed, _reconfigurations;
    private long _skippedBytes, _discardedLines, _captureSkipped;
    private string? _captureError;
    private StreamingHealthSnapshot _current;
    public string CapturePath { get; }
    public StreamingHealthSnapshot Current { get { lock (_gate) return _current; } }

    public StreamingHealth(string steamLogsDirectory, string? diagnosticsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamLogsDirectory);
        var logs = Path.GetFullPath(steamLogsDirectory);
        var diagnostics = Path.GetFullPath(diagnosticsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamLinkVRSettings", "Diagnostics"));
        CapturePath = Path.Combine(diagnostics, "streaming-health.jsonl");
        _driver = new Tail(Path.Combine(logs, DriverName), DriverName);
        _compositor = new Tail(Path.Combine(logs, CompositorName), CompositorName);
        _current = Snapshot(DateTimeOffset.Now);
    }

    public static StreamingHealth FromSteamRoot(string steamRoot, string? diagnosticsDirectory = null)
        => new(Path.Combine(steamRoot, "logs"), diagnosticsDirectory);

    public Task<StreamingHealthSnapshot> PollAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Poll(cancellationToken), cancellationToken);

    public StreamingHealthSnapshot Poll(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observed = DateTimeOffset.Now;
            var capture = new List<StreamingHealthEvent>();
            ReadTail(_driver, observed, capture);
            ReadTail(_compositor, observed, capture);
            Persist(capture);
            return _current = Snapshot(observed);
        }
    }

    private StreamingHealthSnapshot Snapshot(DateTimeOffset observed)
    {
        var summary = _total == 0 ? "No new streaming log events recorded."
            : FormattableString.Invariant($"Recorded {_links} bad-link events, {_encoder} encoder warnings, {_compositorTimeouts} compositor acquire timeouts, {_resets} stream recovery requests and {_reconfigurations} encoder reconfigurations. These are not exact headset dropped-frame counts.");
        return new(observed, _total, _resets, _links, _encoder, _compositorTimeouts,
            _suppressed, _reconfigurations, _skippedBytes, _discardedLines, _captureSkipped,
            _driver.Available, _compositor.Available, summary, CapturePath, _captureError,
            Array.AsReadOnly(_recent.ToArray()));
    }

    private void ReadTail(Tail tail, DateTimeOffset observed, List<StreamingHealthEvent> capture)
    {
        long readFrom = -1;
        try
        {
            using var stream = new FileStream(tail.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            tail.Available = true;
            var created = File.GetCreationTimeUtc(stream.SafeFileHandle);
            if (!tail.Initialized)
            {
                tail.Reset(created, tail.SkipExistingOnFirstOpen ? stream.Length : 0);
                if (tail.SkipExistingOnFirstOpen) tail.DiscardUntilNewline = EndsWithPartialLine(stream);
            }
            else if (created != tail.Created || stream.Length < tail.Position || !AnchorMatches(stream, tail))
            {
                // Creation time plus a small consumed-byte anchor also catches truncate-and-regrow
                // and replacements whose creation time is preserved by Windows file tunneling.
                tail.Reset(created, 0);
            }
            if (stream.Length - tail.Position > MaximumBacklogBytes)
            {
                var start = stream.Length - MaximumBacklogBytes;
                _skippedBytes += start - tail.Position;
                tail.Position = start;
                tail.Line.Clear();
                tail.DiscardUntilNewline = true;
            }
            stream.Position = readFrom = tail.Position;
            var remaining = Math.Min(MaximumBytesPerLogPerPoll, stream.Length - tail.Position);
            var buffer = new byte[4096];
            while (remaining > 0)
            {
                var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0) break;
                tail.Position += count;
                remaining -= count;
                for (var i = 0; i < count; i++)
                {
                    var value = buffer[i];
                    if (value == (byte)'\n')
                    {
                        if (!tail.DiscardUntilNewline)
                        {
                            var line = Encoding.UTF8.GetString(tail.Line.ToArray()).TrimEnd('\r');
                            if (TryParseLine(tail.Name, line, observed, out var item)) Record(item!, capture);
                        }
                        tail.Line.Clear();
                        tail.DiscardUntilNewline = false;
                    }
                    else if (!tail.DiscardUntilNewline)
                    {
                        if (tail.Line.Count >= MaximumLineBytes)
                        {
                            tail.Line.Clear(); tail.DiscardUntilNewline = true; ++_discardedLines;
                        }
                        else tail.Line.Add(value);
                    }
                }
            }
            SaveAnchor(stream, tail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tail.Available = false; // Retry on the next poll; do not replay existing bytes.
            // Bytes consumed before the failure were already parsed and counted. Keep their
            // position and drop the anchor that described the old one, so the next poll
            // continues rather than treating the log as replaced and re-reading it.
            if (readFrom >= 0 && tail.Position != readFrom) tail.Anchor = [];
        }
    }

    private void Record(StreamingHealthEvent item, List<StreamingHealthEvent> capture)
    {
        ++_total;
        switch (item.Category)
        {
            case StreamingHealthCategory.StreamResetRequest: ++_resets; break;
            case StreamingHealthCategory.NetworkBadLink: ++_links; break;
            case StreamingHealthCategory.EncoderPerformance: ++_encoder; break;
            case StreamingHealthCategory.CompositorAcquireTimeout:
                ++_compositorTimeouts; _suppressed += item.SuppressedCount ?? 0; break;
            case StreamingHealthCategory.EncoderReconfiguration: ++_reconfigurations; break;
        }
        if (_recent.Count == MaximumRecentEvents) _recent.Dequeue();
        _recent.Enqueue(item);
        if (capture.Count < MaximumCapturedEventsPerPoll) capture.Add(item);
        else ++_captureSkipped;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private void Persist(List<StreamingHealthEvent> events)
    {
        if (events.Count == 0) return;
        FileStream? stream = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CapturePath)!);
            foreach (var item in events)
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, JsonOptions) + "\n");
                stream ??= CaptureFile.OpenAppend(CapturePath);
                if (stream.Length + bytes.Length > MaximumCaptureFileBytes)
                {
                    stream.Dispose(); stream = null;
                    if (File.Exists(CapturePath + ".1")) File.Move(CapturePath + ".1", CapturePath + ".2", true);
                    File.Move(CapturePath, CapturePath + ".1", true);
                    stream = new FileStream(CapturePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }
                stream.Write(bytes);
            }
            stream?.Flush();
            _captureError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _captureError = "Diagnostic capture is unavailable.";
        }
        finally
        {
            try { stream?.Dispose(); }
            catch (IOException) { _captureError = "Diagnostic capture is unavailable."; }
        }
    }

    private sealed class Tail
    {
        public string Path { get; }
        public string Name { get; }
        public bool Initialized, Available, DiscardUntilNewline;
        public bool SkipExistingOnFirstOpen { get; }
        public DateTime Created;
        public long Position;
        public byte[] Anchor = [];
        public List<byte> Line = new(MaximumLineBytes);
        public Tail(string path, string name)
        {
            Path = path; Name = name; SkipExistingOnFirstOpen = File.Exists(path);
            if (!SkipExistingOnFirstOpen) return;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Reset(File.GetCreationTimeUtc(stream.SafeFileHandle), stream.Length);
                DiscardUntilNewline = EndsWithPartialLine(stream);
                SaveAnchor(stream, this); Available = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        public void Reset(DateTime created, long position)
        {
            Initialized = true; Created = created; Position = position;
            Anchor = []; Line.Clear(); DiscardUntilNewline = false;
        }
    }

    private static bool EndsWithPartialLine(FileStream stream)
    {
        if (stream.Length == 0) return false;
        stream.Position = stream.Length - 1;
        return stream.ReadByte() != '\n';
    }
    private static bool AnchorMatches(FileStream stream, Tail tail)
    {
        if (tail.Anchor.Length == 0) return true;
        var anchor = new byte[tail.Anchor.Length];
        stream.Position = tail.Position - anchor.Length;
        return stream.ReadAtLeast(anchor, anchor.Length, false) == anchor.Length && anchor.AsSpan().SequenceEqual(tail.Anchor);
    }
    private static void SaveAnchor(FileStream stream, Tail tail)
    {
        var size = (int)Math.Min(64, tail.Position);
        var anchor = new byte[size];
        stream.Position = tail.Position - size;
        var read = stream.ReadAtLeast(anchor, size, false);
        tail.Anchor = read == size ? anchor : [];
    }

    private static Regex Pattern(string expression) => new(expression,
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(25));
    private const string Number = @"[0-9]{1,10}(?:\.[0-9]{1,9})?";
    private static readonly Regex Header = Pattern(@"^(?<time>[A-Za-z]{3} [A-Za-z]{3} [0-9]{1,2} [0-9]{4} [0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3})\s+\[[A-Za-z]{1,16}\]\s+-\s+");
    private static readonly Regex ResetRequest = Pattern(@"^Reset video stream because we had a stream reset request(?: \[wgpReset:(?<request>[0-9]{1,10}) != lastReset:(?<previous>[0-9]{1,10})\])?(?:\s|$)");
    private static readonly Regex FrameIdReset = Pattern(@"^Reset video stream because frame ID was not what we expected\. wgp id \((?<request>[0-9]{1,10})\) - last id \((?<previous>[0-9]{1,10})\)(?:\s|$)");
    private static readonly Regex BadLink = Pattern(@"^Bad link event: d=(?<duration>" + Number + @") ms (?<before>" + Number + @") mbit/s -> (?<after>" + Number + @") mbit/s, ping (?<ping>" + Number + @") ms(?:\s|,|$)");
    private static readonly Regex EncoderPerf = Pattern(@"^\[Perf warning\]\s+(?<duration>" + Number + @")\s*ms\s+-\s+(?<stage>Encode Time|videonvenc::SubmitFrame|videonvenc::GetEncodedPacket|videonvenc::StartEncodeData|StartEncodeData::nvEncReconfigureEncoder|StartEncodeData::nvEncEncodePicture|Submitting EOS encode)(?:\s|$)");
    private static readonly Regex SubmitDelay = Pattern(@"^Perf warning: Getting from SubmitFrame to StartEncode took (?<duration>" + Number + @")\s*ms(?:\s|$)");
    private static readonly Regex Reconfiguration = Pattern(@"^Starting NVENC reset configuration Cause Mask: (?<mask>[01]{1,32})(?:\s|$)");
    private static readonly Regex AcquireTimeout = Pattern(@"^WaitForAcquire timed out \([A-Za-z_]{1,32}\); rendering the next frame before the driver took the sync texture(?:\s|$)");
    private static readonly Regex Suppressed = Pattern(@"\[(?<count>[0-9]{1,10}) messages suppressed\]");

    public static bool TryParseLine(string source, string line, DateTimeOffset observedAt, out StreamingHealthEvent? item)
    {
        item = null;
        if (source is not (DriverName or CompositorName) || line.Length > MaximumLineBytes) return false;
        try
        {
            var header = Header.Match(line);
            var timestamp = observedAt;
            DateTime date = default;
            var fromLog = header.Success && DateTime.TryParseExact(header.Groups["time"].Value,
                ["ddd MMM dd yyyy HH:mm:ss.fff", "ddd MMM d yyyy HH:mm:ss.fff"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date);
            if (fromLog)
            {
                try { timestamp = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)); }
                catch (ArgumentException) { fromLog = false; } // Out-of-range/corrupt log dates use observation time.
            }
            var body = header.Success ? line[header.Length..] : line;
            if (source == DriverName)
            {
                var match = ResetRequest.Match(body);
                if (match.Success)
                {
                    item = new(timestamp, observedAt, fromLog, source, StreamingHealthCategory.StreamResetRequest,
                        "Client stream recovery was requested; this is not a measured dropped-frame count.",
                        ResetRequestId: Count(match, "request"), PreviousResetId: Count(match, "previous"));
                    return true;
                }
                match = FrameIdReset.Match(body);
                if (match.Success)
                {
                    item = new(timestamp, observedAt, fromLog, source, StreamingHealthCategory.StreamResetRequest,
                        "Stream was reset because the received frame ID was unexpected; this follows a stream restart or lost frames, not a measured dropped-frame count.",
                        ResetRequestId: Count(match, "request"), PreviousResetId: Count(match, "previous"));
                    return true;
                }
                match = BadLink.Match(body);
                if (match.Success && Numeric(match, "duration") is { } delay && Numeric(match, "ping") is { } ping &&
                    Numeric(match, "before") is { } before && Numeric(match, "after") is { } after)
                {
                    item = new(timestamp, observedAt, fromLog, source, StreamingHealthCategory.NetworkBadLink,
                        FormattableString.Invariant($"Driver reported a bad-link event with delay {delay:0.###} ms and ping {ping:0.###} ms."),
                        delay, ping, before, after); return true;
                }
                match = EncoderPerf.Match(body);
                if (match.Success && Numeric(match, "duration") is { } duration)
                {
                    var stage = match.Groups["stage"].Value; // Exact allowlisted label, never arbitrary log text.
                    item = new(timestamp, observedAt, fromLog, source, StreamingHealthCategory.EncoderPerformance,
                        FormattableString.Invariant($"Encoder performance warning: {stage} took {duration:0.###} ms."), duration);
                    return true;
                }
                match = SubmitDelay.Match(body);
                if (match.Success && Numeric(match, "duration") is { } delayMs)
                {
                    item = new(timestamp, observedAt, fromLog, source, StreamingHealthCategory.EncoderPerformance,
                        FormattableString.Invariant($"Encoder performance warning: SubmitFrame to StartEncode took {delayMs:0.###} ms."), delayMs);
                    return true;
                }
                match = Reconfiguration.Match(body);
                if (match.Success)
                {
                    item = new(timestamp, observedAt, fromLog, source, StreamingHealthCategory.EncoderReconfiguration,
                        "Encoder reconfiguration started; settings or resolution changes can legitimately trigger it.",
                        CauseMask: match.Groups["mask"].Value); return true;
                }
            }
            else if (AcquireTimeout.IsMatch(body))
            {
                var suppressed = Count(Suppressed.Match(body), "count") ?? 0;
                item = new(timestamp, observedAt, fromLog, source, StreamingHealthCategory.CompositorAcquireTimeout,
                    "Compositor timed out waiting for driver texture acquisition; suppressed messages are repeated warnings, not headset dropped frames.",
                    SuppressedCount: suppressed); return true;
            }
        }
        catch (RegexMatchTimeoutException) { }
        return false;
    }
    private static double? Numeric(Match match, string group)
        => double.TryParse(match.Groups[group].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value) && value is >= 0 and <= 1_000_000_000 ? value : null;
    private static long? Count(Match match, string group)
        => long.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value is >= 0 and <= 1_000_000_000 ? value : null;
}
