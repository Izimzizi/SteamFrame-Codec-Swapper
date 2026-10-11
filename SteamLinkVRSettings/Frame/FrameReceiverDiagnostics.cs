using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteamLinkVRSettings.Frame;

public enum FrameReceiverCategory
{
    RetryRequest, BacktrackWindowReset, UnrecoverableError,
    LateMetadata, EmptyDecodeBuffer, DecodeBufferMismatch, DecoderQueueFailure
}

// Retry packets may be requested repeatedly. These events are receiver evidence,
// not unique packet-loss totals or exact headset dropped-frame counts.
public sealed record FrameReceiverEvent(
    DateTimeOffset Timestamp, DateTimeOffset ObservedAt, bool TimestampFromLog,
    FrameReceiverCategory Category, string Message,
    long? PacketCount = null, long? FrameIndex = null, long? ErrorNumber = null);

public sealed record FrameReceiverSnapshot(
    DateTimeOffset ObservedAt, bool Available, string Message,
    int? StreamPid, long? StreamStartTicks, string? LogGeneration,
    long TotalEvents, long RetryEvents, long RequestedRetryPackets,
    long BacktrackWindowResets, long UnrecoverableErrors, long LateMetadataEvents,
    long EmptyDecodeBuffers, long DecodeBufferMismatches, long DecoderQueueFailures,
    long SkippedBytes, long DiscardedLines, long CaptureSkippedEvents,
    string CapturePath, string? CaptureError,
    IReadOnlyList<FrameReceiverEvent> NewEvents,
    IReadOnlyList<FrameReceiverEvent> RecentEvents);

/// <summary>
/// Reads new receiver log bytes through paired, strictly host-verified SSH.
/// No root, forwarded port, device configuration, helper installation, or raw log capture.
/// Existing logs start at EOF; a new log generation is read from its beginning.
/// Counters cover this collector session and continue across receiver restarts.
/// </summary>
public sealed class FrameReceiverDiagnostics
{
    public const int MaximumBytesPerPoll = 256 * 1024;
    public const int MaximumRecentEvents = 128;
    public const int MaximumEventsPerPoll = 256;
    public const int MaximumCaptureFileBytes = 1024 * 1024;
    private const int MaximumResponseCharacters = 192 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly FramePreferences _preferences;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly object _snapshotGate = new();
    private readonly Queue<FrameReceiverEvent> _recent = new();
    private readonly Func<string, CancellationToken, Task<string?>>? _testTransport;
    private Cursor? _cursor;
    private string? _connectionIdentity;
    private long _total, _retry, _packets, _backtrack, _unrecoverable, _late, _empty, _mismatch, _queueFailure;
    private long _skipped, _discarded, _captureSkipped;
    private string? _captureError;
    private FrameReceiverSnapshot _current;
    public string CapturePath { get; }
    public FrameReceiverSnapshot Current { get { lock (_snapshotGate) return _current; } }

    public FrameReceiverDiagnostics(FramePreferences preferences, string? diagnosticsDirectory = null)
        : this(preferences, diagnosticsDirectory, null) { }

    internal FrameReceiverDiagnostics(FramePreferences preferences, string? diagnosticsDirectory,
        Func<string, CancellationToken, Task<string?>>? testTransport)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _testTransport = testTransport;
        CapturePath = Path.Combine(Path.GetFullPath(diagnosticsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamLinkVRSettings", "Diagnostics")), "frame-receiver-health.jsonl");
        _current = Snapshot(DateTimeOffset.Now, false, "Receiver diagnostics have not been polled.", null, null, null, []);
    }

    public async Task<FrameReceiverSnapshot> PollAsync(CancellationToken cancellationToken = default)
    {
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Changing the selected paired endpoint starts its existing log at EOF.
            var identity = string.Join('\n', _preferences.SshHost, _preferences.SshKey, _preferences.SshKnownHosts);
            if (_connectionIdentity != identity) { _cursor = null; _connectionIdentity = identity; }
            string? json;
            try
            {
                var script = BuildRemoteScript(_cursor);
                json = _testTransport is null
                    ? await ReadSshAsync(script, cancellationToken).ConfigureAwait(false)
                    : await _testTransport(script, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { json = null; }
            var observed = DateTimeOffset.Now;
            if (json is null || json.Length > MaximumResponseCharacters)
                return Publish(Snapshot(observed, false, "Receiver diagnostics unavailable; check paired SSH and its trusted host file.", null, null, Current.LogGeneration, []));
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.GetProperty("protocol").GetInt32() != 1) throw new JsonException();
                var cursor = JsonSerializer.Deserialize<Cursor>(root.GetProperty("cursor"), JsonOptions);
                if (cursor is null || !cursor.IsValid()) throw new JsonException();
                var available = root.GetProperty("available").GetBoolean();
                var pid = ReadNullableLong(root, "streamPid", 2, int.MaxValue);
                var ticks = ReadNullableLong(root, "streamStartTicks", 1, long.MaxValue);
                if (pid is null) ticks = null;
                var items = new List<FrameReceiverEvent>();
                foreach (var item in root.GetProperty("events").EnumerateArray())
                {
                    if (items.Count >= MaximumEventsPerPoll) throw new JsonException();
                    items.Add(ParseEvent(item, observed));
                }
                var counts = root.GetProperty("counts");
                long retry = ReadCount(counts, "retry"), backtrack = ReadCount(counts, "backtrack"),
                    unrecoverable = ReadCount(counts, "unrecoverable"), late = ReadCount(counts, "late"),
                    empty = ReadCount(counts, "empty"), mismatch = ReadCount(counts, "mismatch"),
                    queueFailure = ReadCount(counts, "queue");
                var packets = ReadCount(counts, "packets", 100_000_000_000);
                var sum = retry + backtrack + unrecoverable + late + empty + mismatch + queueFailure;
                if (items.Count > sum || (retry == 0 && packets != 0)) throw new JsonException();
                foreach (FrameReceiverCategory category in Enum.GetValues<FrameReceiverCategory>())
                {
                    var expected = category switch { FrameReceiverCategory.RetryRequest => retry,
                        FrameReceiverCategory.BacktrackWindowReset => backtrack, FrameReceiverCategory.UnrecoverableError => unrecoverable,
                        FrameReceiverCategory.LateMetadata => late, FrameReceiverCategory.EmptyDecodeBuffer => empty,
                        FrameReceiverCategory.DecodeBufferMismatch => mismatch, _ => queueFailure };
                    if (items.Count(x => x.Category == category) > expected) throw new JsonException();
                }
                var skipped = ReadCount(root, "skippedBytes", long.MaxValue);
                var discarded = ReadCount(root, "discardedLines");
                var omitted = ReadCount(root, "omittedEvents");
                if (omitted != sum - items.Count || (!available && sum != 0)) throw new JsonException();
                // Commit only after a fully validated response. Failed SSH never advances the cursor.
                _cursor = cursor;
                _total += sum; _retry += retry; _packets += packets; _backtrack += backtrack;
                _unrecoverable += unrecoverable; _late += late; _empty += empty; _mismatch += mismatch; _queueFailure += queueFailure;
                _skipped += skipped; _discarded += discarded; _captureSkipped += omitted;
                foreach (var item in items)
                {
                    _recent.Enqueue(item);
                    while (_recent.Count > MaximumRecentEvents) _recent.Dequeue();
                }
                Persist(items);
                var message = available
                    ? FormattableString.Invariant($"Receiver: {_retry} retry requests ({_packets} requested packets), {_backtrack} backtrack resets, {_unrecoverable} unrecoverable errors, {_late} late metadata, {_empty} missing buffers, {_mismatch} buffer mismatches and {_queueFailure} decoder queue failures. These are not exact dropped-frame counts.")
                    : "The Frame is reachable; its receiver log is unavailable.";
                var generation = cursor.Inode == 0 ? null : FormattableString.Invariant($"{cursor.Device}:{cursor.Inode}:{cursor.Ctime}:{cursor.Generation}");
                return Publish(Snapshot(observed, available, message, (int?)pid, ticks, generation, items));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
            {
                return Publish(Snapshot(observed, false, "Receiver diagnostics returned an unsupported response.", null, null, Current.LogGeneration, []));
            }
        }
        finally { _pollGate.Release(); }
    }

    private FrameReceiverSnapshot Publish(FrameReceiverSnapshot snapshot)
    { lock (_snapshotGate) return _current = snapshot; }

    private FrameReceiverSnapshot Snapshot(DateTimeOffset observed, bool available, string message,
        int? pid, long? ticks, string? generation, IReadOnlyList<FrameReceiverEvent> added)
        => new(observed, available, message, pid, ticks, generation, _total, _retry, _packets,
            _backtrack, _unrecoverable, _late, _empty, _mismatch, _queueFailure, _skipped, _discarded, _captureSkipped,
            CapturePath, _captureError, Array.AsReadOnly(added.ToArray()), Array.AsReadOnly(_recent.ToArray()));

    private static long ReadCount(JsonElement element, string name, long maximum = 100_000)
    {
        var value = element.GetProperty(name).GetInt64();
        if (value < 0 || value > maximum) throw new JsonException();
        return value;
    }

    private static long? ReadNullableLong(JsonElement element, string name, long minimum, long maximum)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        var value = property.GetInt64();
        if (value < minimum || value > maximum) throw new JsonException();
        return value;
    }

    private static FrameReceiverEvent ParseEvent(JsonElement item, DateTimeOffset observed)
    {
        var category = item.GetProperty("category").GetString() switch
        {
            "retry" => FrameReceiverCategory.RetryRequest,
            "backtrack" => FrameReceiverCategory.BacktrackWindowReset,
            "unrecoverable" => FrameReceiverCategory.UnrecoverableError,
            "late" => FrameReceiverCategory.LateMetadata,
            "empty" => FrameReceiverCategory.EmptyDecodeBuffer,
            "mismatch" => FrameReceiverCategory.DecodeBufferMismatch,
            "queue" => FrameReceiverCategory.DecoderQueueFailure,
            _ => throw new JsonException()
        };
        var packets = ReadNullableLong(item, "packetCount", 0, 1_000_000);
        var frame = ReadNullableLong(item, "frameIndex", 0, int.MaxValue);
        var error = ReadNullableLong(item, "errorNumber", 1, 4095);
        if (category != FrameReceiverCategory.RetryRequest && packets is not null) throw new JsonException();
        if (category == FrameReceiverCategory.RetryRequest && packets is null) throw new JsonException();
        if (category is not (FrameReceiverCategory.LateMetadata or FrameReceiverCategory.DecodeBufferMismatch) && frame is not null) throw new JsonException();
        if ((category == FrameReceiverCategory.DecoderQueueFailure) != (error is not null)) throw new JsonException();
        var timestamp = observed;
        var fromLog = false;
        if (item.TryGetProperty("timestamp", out var time) && time.ValueKind == JsonValueKind.String)
        {
            var value = time.GetString();
            if (value?.Length <= 40 && DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.ffffffzzz",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            { timestamp = parsed; fromLog = true; }
        }
        var message = category switch
        {
            FrameReceiverCategory.RetryRequest => FormattableString.Invariant($"Receiver requested retry of {packets} packets; requests can repeat packets."),
            FrameReceiverCategory.BacktrackWindowReset => "Receiver reset its backtrack window.",
            FrameReceiverCategory.UnrecoverableError => "Receiver FEC reported an unrecoverable error.",
            FrameReceiverCategory.LateMetadata => "Receiver received late frame metadata.",
            FrameReceiverCategory.EmptyDecodeBuffer => "Decoder submission found no mapped frame buffer.",
            FrameReceiverCategory.DecoderQueueFailure => "Decoder input queue submission failed.",
            _ => "Decoder submission reported a buffer index mismatch."
        };
        return new(timestamp, observed, fromLog, category, message, packets, frame, error);
    }

    private void Persist(IReadOnlyList<FrameReceiverEvent> events)
    {
        if (events.Count == 0) return;
        _captureError = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CapturePath)!);
            foreach (var item in events)
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, JsonOptions) + "\n");
                if (File.Exists(CapturePath) && new FileInfo(CapturePath).Length + bytes.Length > MaximumCaptureFileBytes)
                {
                    File.Delete(CapturePath + ".2");
                    if (File.Exists(CapturePath + ".1")) File.Move(CapturePath + ".1", CapturePath + ".2");
                    File.Move(CapturePath, CapturePath + ".1");
                }
                using var output = new FileStream(CapturePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                output.Write(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { _captureError = "Receiver diagnostic capture is unavailable."; }
    }

    /// <summary>
    /// The single SSH launch configuration for every headset connection: system OpenSSH, the
    /// configured key and known-hosts file only, strict host checking, no user config.
    /// </summary>
    internal static ProcessStartInfo CreateSshStartInfo(FramePreferences preferences, params string[] remoteCommand)
    {
        if (remoteCommand.Length == 0) remoteCommand = ["python3", "-"];
        var host = preferences.SshHost;
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Any(char.IsWhiteSpace) ||
            !(IPAddress.TryParse(host, out _) || Regex.IsMatch(host, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?\z")))
            throw new ArgumentException("A paired Frame SSH host is required.");
        if (!Path.IsPathFullyQualified(preferences.SshKey) || !File.Exists(preferences.SshKey) ||
            !Path.IsPathFullyQualified(preferences.SshKnownHosts) || !File.Exists(preferences.SshKnownHosts))
            throw new ArgumentException("A paired key and existing trusted host file are required.");
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-F", "NUL", "-T", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes",
            "-o", "StrictHostKeyChecking=yes", "-o", "UserKnownHostsFile=\"" + preferences.SshKnownHosts.Replace('\\', '/') + "\"",
            "-o", "GlobalKnownHostsFile=NUL", "-o", "ConnectTimeout=4", "-o", "ConnectionAttempts=1",
            "-o", "ServerAliveInterval=2", "-o", "ServerAliveCountMax=2", "-i", preferences.SshKey,
            "steamos@" + host }.Concat(remoteCommand)) info.ArgumentList.Add(argument);
        return info;
    }

    private async Task<string?> ReadSshAsync(string script, CancellationToken cancellationToken)
    {
        ProcessStartInfo info;
        try { info = CreateSshStartInfo(_preferences); }
        catch (ArgumentException) { return null; }
        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        if (!process.Start()) return null;
        var output = ReadBoundedAsync(process.StandardOutput, MaximumResponseCharacters, timeout.Token);
        var errors = DiscardAsync(process.StandardError, timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync(script.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var result = await output.ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            return process.ExitCode == 0 ? result : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            timeout.Cancel();
            try { await Task.WhenAll(output, errors).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        }
    }

    private static async Task<string?> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; var overflow = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (!overflow && text.Length + read <= maximum) text.Append(buffer, 0, read);
            else overflow = true; // Continue draining without retaining arbitrary excess output.
        }
        return overflow ? null : text.ToString();
    }

    private static async Task DiscardAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) > 0) { }
    }

    internal sealed record Cursor(bool Initialized, long Device, long Inode, long Ctime,
        long Offset, string Anchor, bool Discard, long Generation)
    {
        public bool IsValid() => Device >= 0 && Inode >= 0 && Ctime >= 0 && Offset >= 0 && Generation >= 0
            && Anchor is not null && (Anchor.Length == 0 || Regex.IsMatch(Anchor, @"\A[0-9a-f]{64}\z"));
    }

    internal static string BuildRemoteScript(Cursor? cursor)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor, JsonOptions)));
        return RemoteScript.Replace("__CURSOR_BASE64__", encoded, StringComparison.Ordinal);
    }

    private const string RemoteScript = """
        import base64, datetime as dt, hashlib, json, os, pathlib, re
        LIMIT=262144
        MAX_BACKLOG=1048576
        MAX_LINE=8192
        MAX_EVENTS=256
        cursor=json.loads(base64.b64decode('__CURSOR_BASE64__',validate=True))
        initial=cursor is None
        if initial:
            cursor=dict(initialized=True,device=0,inode=0,ctime=0,offset=0,anchor='',discard=False,generation=0)
        result=dict(protocol=1,available=False,cursor=cursor,streamPid=None,streamStartTicks=None,
                    counts=dict(retry=0,packets=0,backtrack=0,unrecoverable=0,late=0,empty=0,mismatch=0,queue=0),
                    events=[],skippedBytes=0,discardedLines=0,omittedEvents=0)
        # Never inspect command lines, launch arguments, credentials, or other users' files.
        def receiver_identity():
            try:
                proc=pathlib.Path('/proc')
                for entry in list(proc.iterdir())[:4096]:
                    if not entry.name.isdecimal(): continue
                    try:
                        comm=(entry/'comm').read_text().strip()
                        if comm not in ('vrlink','vrlinkrunthread'): continue
                        uid=next(l for l in (entry/'status').read_text().splitlines() if l.startswith('Uid:')).split()[1:]
                        if not uid or any(int(v)!=1000 for v in uid): continue
                        try:
                            if os.readlink(entry/'exe')!='/opt/steamvr/tools/vrlink/bin/linuxarm64/vrlink': continue
                        except PermissionError: pass
                        stat=(entry/'stat').read_text().rsplit(')',1)[1].split()
                        result['streamPid']=int(entry.name)
                        result['streamStartTicks']=int(stat[19])
                        return
                    except (OSError,StopIteration,ValueError,IndexError): continue
            except OSError: pass
        patterns=[
            ('retry',re.compile(r'SVLFEC: substantive retry \((\d{1,7}) packets\)',re.I)),
            ('backtrack',re.compile(r'Received frame outside of backtrack window\.\s+Resetting\.',re.I)),
            ('unrecoverable',re.compile(r'SVLFEC Received ExperiencedAnUnrecoverableError\(\)',re.I)),
            ('late',re.compile(r'\[SVLFEC\] received late metadata for frame\s*\[\s*(\d{1,10})\s*\]',re.I)),
            ('queue',re.compile(r'SVLCodecV4L2::SubmitFrameForDecode: Called submit with an empty buffer -?\d{1,10} \[(\d{1,4}) [^\]\r\n]*\]',re.I)),
            ('empty',re.compile(r'SVLCodecV4L2::SubmitFrameForDecode: Called submit with an empty buffer\s*$',re.I)),
            ('mismatch',re.compile(r'SVLCodecV4L2::SubmitFrameForDecode: \(idx:\s*(\d{1,10})\) Buffer from the pointer != m_nBufferIdForFrameIndex\.',re.I))]
        def parse(line):
            # Only fixed categories/numbers/timestamps leave this process; raw text is discarded.
            for category,pattern in patterns:
                match=pattern.search(line)
                if not match: continue
                item=dict(category=category)
                if category=='retry':
                    number=int(match[1])
                    if number>1000000: continue
                    item['packetCount']=number
                    result['counts']['packets']+=number
                elif category in ('late','mismatch'):
                    number=int(match[1])
                    if number>2147483647: continue
                    item['frameIndex']=number
                elif category=='queue':
                    number=int(match[1])
                    if not 1<=number<=4095: continue
                    item['errorNumber']=number
                time=re.match(r'^(\w{3} \w{3} +\d{1,2} \d{4} \d{2}:\d{2}:\d{2}\.\d{1,6}) \[',line)
                if time:
                    try:
                        value=dt.datetime.strptime(time[1],'%a %b %d %Y %H:%M:%S.%f').astimezone()
                        item['timestamp']=value.isoformat(timespec='microseconds')
                    except (ValueError,OverflowError,OSError): pass
                result['counts'][category]+=1
                if len(result['events'])<MAX_EVENTS: result['events'].append(item)
                else: result['omittedEvents']+=1
                break
        def anchor(stream,offset):
            stream.seek(max(0,offset-64))
            return hashlib.sha256(stream.read(min(64,offset))).hexdigest()
        try:
            path=pathlib.Path.home()/'.steam/steam/logs/vrlink.txt'
            with path.open('rb') as stream:
                stat=os.fstat(stream.fileno())
                size=stat.st_size
                same=(cursor['device']==stat.st_dev and cursor['inode']==stat.st_ino and size>=cursor['offset'])
                if same and cursor['anchor']:
                    same=anchor(stream,cursor['offset'])==cursor['anchor']
                if not same:
                    cursor=dict(initialized=True,device=stat.st_dev,inode=stat.st_ino,ctime=stat.st_ctime_ns,
                                offset=size if initial else 0,anchor='',discard=False,generation=cursor['generation']+1)
                    result['cursor']=cursor
                    if initial and size:
                        stream.seek(size-1)
                        cursor['discard']=stream.read(1)!=b'\n'
                if size-cursor['offset']>MAX_BACKLOG:
                    result['skippedBytes']=size-MAX_BACKLOG-cursor['offset']
                    cursor['offset']=size-MAX_BACKLOG
                    cursor['discard']=True
                stream.seek(cursor['offset'])
                data=stream.read(min(LIMIT,max(0,size-cursor['offset'])))
                consumed=0
                while True:
                    end=data.find(b'\n',consumed)
                    if end<0: break
                    line=data[consumed:end]
                    if cursor['discard']: cursor['discard']=False
                    elif len(line)>MAX_LINE: result['discardedLines']+=1
                    else: parse(line.rstrip(b'\r').decode('utf-8','replace'))
                    consumed=end+1
                tail=data[consumed:]
                if cursor['discard']:
                    consumed=len(data)
                elif len(tail)>MAX_LINE:
                    consumed=len(data)
                    cursor['discard']=True
                    result['discardedLines']+=1
                # Incomplete short lines remain before the cursor and are reread next time;
                # no raw partial line is returned in cursor JSON.
                cursor['offset']+=consumed
                cursor['anchor']=anchor(stream,cursor['offset'])
                result['available']=True
        except (OSError,ValueError,OverflowError,KeyError,TypeError):
            result['available']=False
            result['counts']=dict(retry=0,packets=0,backtrack=0,unrecoverable=0,late=0,empty=0,mismatch=0,queue=0)
            result['events']=[]
            result['skippedBytes']=result['discardedLines']=result['omittedEvents']=0
            # A failed read cannot consume log bytes. If initially absent, a later new log starts at 0.
            cursor=json.loads(base64.b64decode('__CURSOR_BASE64__',validate=True))
            result['cursor']=cursor or dict(initialized=True,device=0,inode=0,ctime=0,offset=0,anchor='',discard=False,generation=0)
        receiver_identity()
        print(json.dumps(result,separators=(',',':')))
        """;
}
