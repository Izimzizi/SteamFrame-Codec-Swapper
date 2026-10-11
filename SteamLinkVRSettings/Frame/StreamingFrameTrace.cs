using System.Globalization;

namespace SteamLinkVRSettings.Frame;

public sealed record StageTiming(int Samples, double MedianMs, double P99Ms, double MaximumMs);
public sealed record StreamingFrameTraceSnapshot(string FileName, DateTimeOffset? FirstFrame,
    DateTimeOffset? LastFrame, int SentFrames, int NoTransmitRows, int MissingReceiveFeedback,
    int MissingIlluminationFeedback, int LateSubmissionFrames, StageTiming? Encode,
    StageTiming? Transport, StageTiming? Decode, StageTiming? LateSubmission, string Message);

/// <summary>Reads Valve's recovery traces without treating absent feedback as a dropped frame.</summary>
public sealed class StreamingFrameTrace
{
    private readonly string _logsDirectory;
    private string? _lastIdentity;
    private StreamingFrameTraceSnapshot? _last;
    public StreamingFrameTrace(string steamLogsDirectory) => _logsDirectory = steamLogsDirectory;

    public StreamingFrameTraceSnapshot? ReadLatest()
    {
        try
        {
            var file = new DirectoryInfo(_logsDirectory).EnumerateFiles("vrlink_aftm_*.csv")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file is null) return null;
            var identity = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            if (identity == _lastIdentity) return _last;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 4 * 1024 * 1024) return new(file.Name, null, null, 0, 0, 0, 0, 0, null, null, null, null, "Recovery trace exceeds the inspection limit.");
            using var reader = new StreamReader(stream);
            var parsed = Read(reader, file.Name);
            _lastIdentity = identity; _last = parsed;
            return parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    public static StreamingFrameTraceSnapshot Read(TextReader reader, string fileName)
    {
        var header = reader.ReadLine()?.Split(',').Select(x => x.Trim().Trim('"')).ToArray() ?? [];
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Length; i++) if (!columns.TryAdd(header[i], i)) return Empty("Recovery trace has duplicate columns.");
        string[] required = ["Present ID", "TX ID", "WGP Release (realtime)", "Encode Start", "Encode End", "Send Start", "Receive", "Decode Start (remote)", "Decode End (remote)", "Submit Illumination", "Predicted Display"];
        if (required.Any(k => !columns.ContainsKey(k))) return Empty("This recovery trace format is unavailable.");
        var rows = new List<Row>();
        string? line;
        while (rows.Count < 10000 && (line = reader.ReadLine()) != null)
        {
            if (line.Length > 8192) continue;
            var cells = line.Split(',');
            if (cells.Length != header.Length) continue;
            long? Number(string name) => columns.TryGetValue(name, out var index) &&
                long.TryParse(cells[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
            var tx = Number("TX ID"); var realtime = Number("WGP Release (realtime)"); var present = Number("Present ID");
            if (tx is null || realtime is null || realtime <= 0 || present is null) continue;
            rows.Add(new(present.Value, tx.Value, realtime.Value, Number("Encode Start"), Number("Encode End"),
                Number("Send Start"), Number("Receive"), Number("Decode Start (remote)"), Number("Decode End (remote)"),
                Number("Submit Illumination"), Number("Predicted Display")));
        }
        rows.Sort((a, b) => a.Realtime.CompareTo(b.Realtime));
        // Ring-buffer dumps overlap; one transmitted frame can have multiple feedback rows.
        var sent = rows.Where(r => r.Tx >= 0).GroupBy(r => (r.Realtime, r.Tx, r.Present)).Select(g => g.Last()).ToArray();
        var encode = sent.Select(r => Duration(r.EncodeStart, r.EncodeEnd)).OfType<double>().ToArray();
        // Receive is mapped to the sender's clock by Valve; never subtract remote realtime from host realtime.
        var transport = sent.Select(r => Duration(r.SendStart, r.Receive)).OfType<double>().ToArray();
        var decode = sent.Select(r => Duration(r.DecodeStartRemote, r.DecodeEndRemote)).OfType<double>().ToArray();
        var late = sent.Where(r => r.Illumination > 0 && r.Predicted > 0)
            .Select(r => (r.Illumination!.Value - r.Predicted!.Value) / 1000d).Where(v => v > 1 && v < 10000).ToArray();
        var first = rows.Count == 0 ? null : Date(rows[0].Realtime);
        var last = rows.Count == 0 ? null : Date(rows[^1].Realtime);
        return new(fileName, first, last, sent.Length, rows.Count(r => r.Tx < 0),
            sent.Count(r => r.Receive is null or <= 0), sent.Count(r => r.Illumination is null or <= 0), late.Length,
            Timing(encode), Timing(transport), Timing(decode), Timing(late),
            "Timing from the last recovery trace; missing feedback and no-transmit rows are not proven packet drops.");

        StreamingFrameTraceSnapshot Empty(string message) => new(fileName, null, null, 0, 0, 0, 0, 0, null, null, null, null, message);
    }

    private static double? Duration(long? start, long? end) => start > 0 && end >= start && end > 0 && end - start < 10_000_000
        ? (end.Value - start.Value) / 1000d : null;
    private static DateTimeOffset? Date(long microseconds)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    private static StageTiming? Timing(double[] values)
    {
        if (values.Length == 0) return null;
        Array.Sort(values);
        var middle = values.Length / 2;
        var median = values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
        return new(values.Length, median, values[Math.Clamp((int)Math.Ceiling(values.Length * .99) - 1, 0, values.Length - 1)], values[^1]);
    }
    private sealed record Row(long Present, long Tx, long Realtime, long? EncodeStart, long? EncodeEnd, long? SendStart,
        long? Receive, long? DecodeStartRemote, long? DecodeEndRemote, long? Illumination, long? Predicted);
}
