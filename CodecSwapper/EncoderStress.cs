using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace CodecSwapper;

/// <summary>
/// Plans the changes of the temporary encoder stress test (benchmark window, "Debug: stress test"): random live changes of rate control, preset and VBV, and
/// every few changes multipass goes to 2 or back, the pattern that hung the GPU in a real session. The same seed gives the same sequence.
/// </summary>
public sealed class EncoderStressPlan(int seed, int multiPass2Every)
{
    private readonly Random _random = new(seed);
    private int _sinceMultiPass2;

    public int Seed => seed;
    public int Changes { get; private set; }
    /// <summary>Changes since multipass last changed (0 right after a multipass change).</summary>
    public int SinceMultiPassChange => _sinceMultiPass2;

    public EncoderChoice Next(EncoderChoice current)
    {
        Changes++;
        if (multiPass2Every > 0 && Changes % multiPass2Every == 0)
        {
            _sinceMultiPass2 = 0;
            // Mostly the walk 0 -> 1 -> 2 -> 0 that the smooth benchmark makes, sometimes a jump to either other value (2 -> 1, 0 -> 2, ...).
            var multiPass = _random.Next(0, 3) == 0 ? (current.MultiPass + 1 + _random.Next(0, 2)) % 3 : (current.MultiPass + 1) % 3;
            return current with { MultiPass = multiPass };
        }
        _sinceMultiPass2++;
        switch (_random.Next(0, 3))
        {
            case 0:
                int preset; do preset = _random.Next(1, 8); while (preset == current.Preset);
                return current with { Preset = preset };
            case 1:
                return current with { RateControl = current.RateControl == "cbr" ? "vbr" : "cbr" };
            default:
                int vbv; do vbv = _random.Next(1, 9); while (vbv == current.VbvFrames);
                return current with { VbvFrames = vbv };
        }
    }
}

/// <summary>What shows that the GPU or the driver gave up during the stress test.</summary>
public static class EncoderStressSigns
{
    /// <summary>GPU errors Windows logged since the given time (nvlddmkm event 153), the error behind "Error occurred on GPUID".</summary>
    public static int GpuErrorsSince(DateTimeOffset since)
    {
        try
        {
            var time = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var query = new EventLogQuery("System", PathType.LogName, $"*[System[Provider[@Name='nvlddmkm'] and (EventID=153) and TimeCreated[@SystemTime>='{time}']]]");
            using var reader = new EventLogReader(query);
            var count = 0;
            while (reader.ReadEvent() is { } record) { using (record) count++; }
            return count;
        }
        catch (Exception error) when (error is EventLogException or UnauthorizedAccessException or InvalidOperationException) { return 0; }
    }

    /// <summary>The first fatal line the Steam Link driver wrote since the given time, or null (for example "Perf: Frame 5 not in expected state").</summary>
    public static string? DriverFatalSince(string driverLog, DateTimeOffset since)
    {
        try
        {
            if (!File.Exists(driverLog)) return null;
            using var stream = new FileStream(driverLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 400_000) stream.Seek(-400_000, SeekOrigin.End);
            using var text = new StreamReader(stream);
            while (text.ReadLine() is { } line)
            {
                var at = line.IndexOf("[Fatal]", StringComparison.Ordinal);
                if (at < 0 || line.Length < 28) continue;
                if (DateTime.TryParseExact(line[..28], "ddd MMM dd yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when) && when >= since.LocalDateTime)
                    return line[(at + 7)..].Trim(' ', '-');
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
