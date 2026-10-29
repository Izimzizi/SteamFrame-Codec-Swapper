using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteamFrame.Core;

public enum StreamingHealthCategory
{
    StreamResetRequest,
    NetworkBadLink,
    EncoderPerformance,
    CompositorAcquireTimeout,
    EncoderReconfiguration,
    /// <summary>The driver's own periodic link-bandwidth estimate ("Link BW: Est").</summary>
    LinkEstimate,
    /// <summary>The driver's periodic per-frame link-capacity histogram ("Link BW hist").</summary>
    LinkHistogram,
    /// <summary>A frame the sender held back because the link could not take it yet ("THROTTLE EVENT").</summary>
    SenderThrottle,
    /// <summary>The driver's video link to the headset became active or inactive ("Connection active/inactive at").</summary>
    LinkConnection
}

/// <summary>
/// One "Link BW hist" sample: counts of per-frame link-capacity measurements in fixed-width
/// buckets. The last bucket is open-ended and counts every measurement at or above its lower edge.
/// </summary>
public sealed record LinkCapacityHistogram(double BucketMbps, IReadOnlyList<long> Counts)
{
    public const int MaximumBuckets = 128;
    [JsonIgnore] public long TotalSamples => Counts.Sum();
    /// <summary>Lower edge of the open-ended last bucket.</summary>
    [JsonIgnore] public double OverflowEdgeMbps => BucketMbps * Math.Max(0, Counts.Count - 1);
    [JsonIgnore] public double OverflowShare => TotalSamples == 0 ? 0 : Counts[^1] / (double)TotalSamples;
    /// <summary>Share of measurements whose bucket lies entirely below the given rate.</summary>
    public double ShareBelow(double mbps)
    {
        var total = TotalSamples;
        if (total == 0 || BucketMbps <= 0) return 0;
        long below = 0;
        for (var i = 0; i < Counts.Count - 1; i++) if ((i + 1) * BucketMbps <= mbps) below += Counts[i];
        return below / (double)total;
    }
    /// <summary>
    /// Lower edge of the bucket containing the given percentile. AtOrAbove is true when it lands in
    /// the open-ended last bucket: the link measured at least that rate, and the histogram cannot say how much more.
    /// </summary>
    public (double Mbps, bool AtOrAbove) Percentile(double fraction)
    {
        var total = TotalSamples;
        if (total == 0 || Counts.Count == 0) return (0, false);
        var threshold = Math.Clamp(fraction, 0, 1) * total;
        long cumulative = 0;
        for (var i = 0; i < Counts.Count; i++)
        {
            cumulative += Counts[i];
            if (cumulative >= threshold && cumulative > 0) return (i * BucketMbps, i == Counts.Count - 1);
        }
        return (OverflowEdgeMbps, true);
    }
    public static bool IsValid(LinkCapacityHistogram? histogram) => histogram is { BucketMbps: > 0 and <= 1000, Counts.Count: >= 2 and <= MaximumBuckets }
        && histogram.Counts.All(c => c is >= 0 and <= 100_000_000);
}

// Only synthesized text and validated numeric metadata cross the log boundary.
// Timeouts/recovery requests are evidence of a stall, not exact headset dropped-frame counts.
public sealed record StreamingHealthEvent(
    DateTimeOffset Timestamp, DateTimeOffset ObservedAt, bool TimestampFromLog,
    string Source, StreamingHealthCategory Category, string Message,
    double? DurationMs = null, double? PingMs = null,
    double? PreviousBitrateMbps = null, double? NewBitrateMbps = null,
    long? SuppressedCount = null, long? ResetRequestId = null, long? PreviousResetId = null,
    string? CauseMask = null,
    double? EstimateMbps = null, bool? Automatic = null, long? HoldoffSteps = null,
    double? ThrottleMs = null, LinkCapacityHistogram? Histogram = null, bool? LinkActive = null)
{
    /// <summary>True for a bad-link event in which the driver moved the stream to a different bitrate.</summary>
    [JsonIgnore] public bool ChangedBitrate => Category == StreamingHealthCategory.NetworkBadLink
        && PreviousBitrateMbps is double before && NewBitrateMbps is double after && Math.Abs(before - after) >= 0.5;
}

/// <summary>The driver's latest statement of the bitrate it is sending at, and which log line said so.</summary>
public sealed record DriverBitrateReading(double Mbps, bool? Automatic, DateTimeOffset Timestamp, DateTimeOffset ObservedAt, StreamingHealthCategory Source);

public sealed record LinkHistogramReading(LinkCapacityHistogram Histogram, DateTimeOffset Timestamp, DateTimeOffset ObservedAt);

public sealed record StreamingHealthSnapshot(
    DateTimeOffset ObservedAt, long TotalEvents, long StreamResetRequests,
    long NetworkBadLinkEvents, long EncoderPerformanceWarnings,
    long CompositorAcquireTimeouts, long SuppressedCompositorMessages,
    long EncoderReconfigurations, long SkippedBytes, long DiscardedLines,
    long CaptureSkippedEvents, bool DriverLogAvailable, bool CompositorLogAvailable,
    string Summary, string CapturePath, string? CaptureError,
    IReadOnlyList<StreamingHealthEvent> RecentEvents,
    long SenderThrottleEvents = 0, double SenderThrottleMs = 0, long LinkEstimates = 0, long LinkHistograms = 0, long BitrateChanges = 0,
    long ThrottleEventsLastMinute = 0, double ThrottleMsLastMinute = 0,
    DriverBitrateReading? DriverBitrate = null, DriverBitrateReading? LatestEstimate = null,
    StreamingHealthEvent? LatestBitrateChange = null, LinkHistogramReading? LatestHistogram = null)
{
    public bool ServerLogAvailable { get; init; }
    /// <summary>The driver's last statement about its video link; null until one has been seen this session.</summary>
    public bool? LinkActive { get; init; }
    public DateTimeOffset? LinkChangedAt { get; init; }
    /// <summary>Sender throttles of a second or more, or while the link was down: stalls and reconnects, not held frames.</summary>
    public long LinkStalls { get; init; }
}

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
    private const string DriverName = "driver_vrlink.txt", CompositorName = "vrcompositor.txt", ServerName = "vrserver.txt";
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(1);
    /// <summary>A frame held this long is a stall or a reconnect, not a link holding one frame back.</summary>
    public const double StallThrottleMs = 1000;
    private readonly object _gate = new();
    private readonly Tail _driver, _compositor, _server;
    private readonly Queue<StreamingHealthEvent> _recent = new();
    private readonly Queue<(DateTimeOffset At, double Ms)> _throttles = new();
    private long _total, _resets, _links, _encoder, _compositorTimeouts, _suppressed, _reconfigurations;
    private long _throttleEvents, _estimates, _histograms, _bitrateChanges, _linkStalls;
    private double _throttleMs;
    private long _skippedBytes, _discardedLines, _captureSkipped;
    private string? _captureError;
    private DriverBitrateReading? _driverBitrate, _latestEstimate;
    private StreamingHealthEvent? _latestBitrateChange;
    private LinkHistogramReading? _latestHistogram;
    private bool? _linkActive;
    private DateTimeOffset? _linkChangedAt;
    private StreamingHealthSnapshot _current;
    public string CapturePath { get; }
    public StreamingHealthSnapshot Current { get { lock (_gate) return _current; } }

    public StreamingHealth(string steamLogsDirectory, string? diagnosticsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamLogsDirectory);
        var logs = Path.GetFullPath(steamLogsDirectory);
        var diagnostics = Path.GetFullPath(diagnosticsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamFrameTray", "Diagnostics"));
        CapturePath = Path.Combine(diagnostics, "streaming-health.jsonl");
        _driver = new Tail(Path.Combine(logs, DriverName), DriverName);
        _compositor = new Tail(Path.Combine(logs, CompositorName), CompositorName);
        _server = new Tail(Path.Combine(logs, ServerName), ServerName);
        SeedLinkState(_driver.Path);
        _current = Snapshot(DateTimeOffset.Now);
    }

    /// <summary>
    /// Events start at the end of an existing log, but the video link's last state is still worth
    /// knowing: read the tail of the driver log once for its latest "Connection active/inactive" line.
    /// Nothing is counted or captured from it.
    /// </summary>
    private void SeedLinkState(string driverPath)
    {
        try
        {
            using var stream = new FileStream(driverPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var size = (int)Math.Min(stream.Length, 512 * 1024);
            if (size == 0) return;
            stream.Position = stream.Length - size;
            var buffer = new byte[size];
            stream.ReadExactly(buffer);
            var now = DateTimeOffset.Now;
            foreach (var line in Encoding.UTF8.GetString(buffer).Split('\n'))
            {
                var text = line.TrimEnd('\r');
                if (!text.Contains("Connection ", StringComparison.Ordinal)) continue;
                if (TryParseLine(DriverName, text, now, out var item) && item!.Category == StreamingHealthCategory.LinkConnection)
                { _linkActive = item.LinkActive; _linkChangedAt = item.Timestamp; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
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
            ReadTail(_server, observed, capture);
            Persist(capture);
            return _current = Snapshot(observed);
        }
    }

    private StreamingHealthSnapshot Snapshot(DateTimeOffset observed)
    {
        var summary = _total == 0 ? "No new streaming log events recorded."
            : FormattableString.Invariant($"Recorded {_links} bad-link events ({_bitrateChanges} bitrate changes), {_throttleEvents} held frames, {_linkStalls} sender stalls, {_encoder} encoder warnings, {_compositorTimeouts} compositor acquire timeouts, {_resets} stream recovery requests and {_reconfigurations} encoder reconfigurations. These are not exact headset dropped-frame counts.");
        while (_throttles.Count > 0 && observed - _throttles.Peek().At > ThrottleWindow) _throttles.Dequeue();
        return new(observed, _total, _resets, _links, _encoder, _compositorTimeouts,
            _suppressed, _reconfigurations, _skippedBytes, _discardedLines, _captureSkipped,
            _driver.Available, _compositor.Available, summary, CapturePath, _captureError,
            Array.AsReadOnly(_recent.ToArray()),
            _throttleEvents, _throttleMs, _estimates, _histograms, _bitrateChanges,
            _throttles.Count, _throttles.Sum(t => t.Ms),
            _driverBitrate, _latestEstimate, _latestBitrateChange, _latestHistogram) { ServerLogAvailable = _server.Available, LinkActive = _linkActive, LinkChangedAt = _linkChangedAt, LinkStalls = _linkStalls };
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
        // Periodic statistics and per-frame throttles are summarized through dedicated snapshot
        // fields; only notable events enter the recent list shown to the user.
        var notable = true;
        switch (item.Category)
        {
            case StreamingHealthCategory.StreamResetRequest: ++_resets; break;
            case StreamingHealthCategory.NetworkBadLink:
                ++_links;
                if (item.ChangedBitrate) { ++_bitrateChanges; _latestBitrateChange = item; }
                if (item.NewBitrateMbps is double after)
                    _driverBitrate = new(after, item.Automatic, item.Timestamp, item.ObservedAt, item.Category);
                break;
            case StreamingHealthCategory.EncoderPerformance: ++_encoder; break;
            case StreamingHealthCategory.CompositorAcquireTimeout:
                ++_compositorTimeouts; _suppressed += item.SuppressedCount ?? 0; break;
            case StreamingHealthCategory.EncoderReconfiguration: ++_reconfigurations; break;
            case StreamingHealthCategory.LinkEstimate:
                ++_estimates; notable = false;
                if (item.EstimateMbps is double estimate)
                    _driverBitrate = _latestEstimate = new(estimate, item.Automatic, item.Timestamp, item.ObservedAt, item.Category);
                break;
            case StreamingHealthCategory.LinkHistogram:
                ++_histograms; notable = false;
                if (item.Histogram is not null) _latestHistogram = new(item.Histogram, item.Timestamp, item.ObservedAt);
                break;
            case StreamingHealthCategory.SenderThrottle:
                var held = item.ThrottleMs ?? 0;
                if (held >= StallThrottleMs || _linkActive == false)
                {
                    // Multi-second holds and holds while the link is down are stalls or reconnects: shown
                    // as events, but never as held frames that would blame the bitrate.
                    ++_linkStalls;
                    item = item with { Message = FormattableString.Invariant($"Sender stalled for {held / 1000:0.0} s; frames were not being delivered{(_linkActive == false ? " while the video link was down" : "")}.") };
                    break;
                }
                ++_throttleEvents; notable = false;
                _throttleMs += held;
                _throttles.Enqueue((item.ObservedAt, held));
                break;
            case StreamingHealthCategory.LinkConnection:
                _linkActive = item.LinkActive; _linkChangedAt = item.ObservedAt;
                break;
        }
        if (notable)
        {
            if (_recent.Count == MaximumRecentEvents) _recent.Dequeue();
            _recent.Enqueue(item);
        }
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
    private static readonly Regex BadLink = Pattern(@"^Bad link event: d=(?<duration>" + Number + @") ms (?<before>" + Number + @") mbit/s -> (?<after>" + Number + @") mbit/s, ping (?<ping>" + Number + @") ms(?:, holdoff (?<holdoff>[0-9]{1,6}), auto (?<auto>[01]))?(?:\s|,|$)");
    private static readonly Regex LinkEstimate = Pattern(@"^Link BW: Est (?<estimate>" + Number + @") Mbit/s \(auto: (?<auto>[01])\)(?:\s|$)");
    private static readonly Regex LinkHistogram = Pattern(@"^Link BW hist: w (?<width>" + Number + @")\s+(?<counts>(?:[0-9]{1,9},){2,128})\s*$");
    private static readonly Regex Throttle = Pattern(@"^THROTTLE EVENT: (?<microseconds>[0-9]{1,10})(?:\s|$)");
    private static readonly Regex LinkConnection = Pattern(@"^Connection (?<state>active|inactive) at (?<seconds>" + Number + @")(?:\s|$)");
    private static readonly Regex EncoderPerf = Pattern(@"^\[Perf warning\]\s+(?<duration>" + Number + @")\s*ms\s+-\s+(?<stage>Encode Time|videonvenc::SubmitFrame|videonvenc::GetEncodedPacket|videonvenc::StartEncodeData|StartEncodeData::nvEncReconfigureEncoder|StartEncodeData::nvEncEncodePicture|Submitting EOS encode)(?:\s|$)");
    private static readonly Regex SubmitDelay = Pattern(@"^Perf warning: Getting from SubmitFrame to StartEncode took (?<duration>" + Number + @")\s*ms(?:\s|$)");
    private static readonly Regex Reconfiguration = Pattern(@"^Starting NVENC reset configuration Cause Mask: (?<mask>[01]{1,32})(?:\s|$)");
    private static readonly Regex AcquireTimeout = Pattern(@"^WaitForAcquire timed out \([A-Za-z_]{1,32}\); rendering the next frame before the driver took the sync texture(?:\s|$)");
    private static readonly Regex KeyedAcquireTimeout = Pattern(@"^(?:\[Compositor\] )?AcquireSync FAILED with WAIT_TIMEOUT(?:\s|$)");
    private static readonly Regex Suppressed = Pattern(@"\[(?<count>[0-9]{1,10}) messages suppressed\]");

    public static bool TryParseLine(string source, string line, DateTimeOffset observedAt, out StreamingHealthEvent? item)
    {
        item = null;
        if (source is not (DriverName or CompositorName or ServerName) || line.Length > MaximumLineBytes) return false;
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
            // vrserver mirrors many driver messages. Only collect this specific
            // texture warning there; recovery/performance events already come
            // from driver_vrlink and must not be counted again.
            if (source == ServerName)
            {
                if (!body.StartsWith("vrlink: ", StringComparison.Ordinal)) return false;
                body = body[8..];
            }
            if (KeyedAcquireTimeout.IsMatch(body))
            {
                item = new(timestamp, observedAt, fromLog, source, StreamingHealthCategory.CompositorAcquireTimeout,
                    "PC shared-texture acquisition timed out; this can delay frame delivery before encoding.");
                return true;
            }
            if (source == ServerName) return false;
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
                    var holdoff = match.Groups["holdoff"].Success ? Count(match, "holdoff") : null;
                    bool? automatic = match.Groups["auto"].Success ? match.Groups["auto"].Value == "1" : null;
                    var message = Math.Abs(before - after) >= 0.5
                        ? FormattableString.Invariant($"SteamVR {(after < before ? "lowered" : "raised")} the stream from {before:0.#} to {after:0.#} Mbps after a {delay:0.#} ms link stall (ping {ping:0.#} ms).")
                        : FormattableString.Invariant($"Link stall of {delay:0.#} ms at {before:0.#} Mbps (ping {ping:0.#} ms); the bitrate was not changed.");
                    item = new(timestamp, observedAt, fromLog, source, StreamingHealthCategory.NetworkBadLink, message,
                        delay, ping, before, after, HoldoffSteps: holdoff, Automatic: automatic); return true;
                }
                match = LinkEstimate.Match(body);
                if (match.Success && Numeric(match, "estimate") is { } estimate)
                {
                    var automatic = match.Groups["auto"].Value == "1";
                    item = new(timestamp, observedAt, fromLog, source, StreamingHealthCategory.LinkEstimate,
                        FormattableString.Invariant($"SteamVR link estimate {estimate:0.#} Mbps ({(automatic ? "automatic bitrate" : "manual target")})."),
                        EstimateMbps: estimate, Automatic: automatic); return true;
                }
                match = LinkHistogram.Match(body);
                if (match.Success && Numeric(match, "width") is { } width && width > 0 && width <= 1000)
                {
                    var counts = new List<long>();
                    foreach (var text in match.Groups["counts"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count > 100_000_000) return false;
                        counts.Add(count);
                    }
                    var histogram = new LinkCapacityHistogram(width, counts.AsReadOnly());
                    if (!LinkCapacityHistogram.IsValid(histogram)) return false;
                    var p10 = histogram.Percentile(0.10);
                    item = new(timestamp, observedAt, fromLog, source, StreamingHealthCategory.LinkHistogram,
                        FormattableString.Invariant($"Link capacity histogram: {histogram.TotalSamples} frames, {histogram.OverflowShare:P0} at or above {histogram.OverflowEdgeMbps:0} Mbps, 10th percentile {(p10.AtOrAbove ? "at least " : "")}{p10.Mbps:0} Mbps."),
                        Histogram: histogram); return true;
                }
                match = Throttle.Match(body);
                if (match.Success && Count(match, "microseconds") is { } microseconds)
                {
                    var ms = microseconds / 1000.0;
                    item = new(timestamp, observedAt, fromLog, source, StreamingHealthCategory.SenderThrottle,
                        FormattableString.Invariant($"Sender held a frame for {ms:0.0} ms because the link could not take it yet."),
                        ThrottleMs: ms); return true;
                }
                match = LinkConnection.Match(body);
                if (match.Success && Numeric(match, "seconds") is not null)
                {
                    var active = match.Groups["state"].Value == "active";
                    item = new(timestamp, observedAt, fromLog, source, StreamingHealthCategory.LinkConnection,
                        active ? "The video link to the headset became active." : "The video link to the headset became inactive; the headset stopped or paused streaming.",
                        LinkActive: active); return true;
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
                    "Compositor timed out waiting for the driver's handoff event; this does not prove shared-texture acquisition failed or that the headset dropped frames.",
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
