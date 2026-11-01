using System.Globalization;

namespace CodecSwapper;

/// <summary>How long the encoder took per frame over the last second, from submitting a picture to its bitstream being ready. The hook writes it once a second.</summary>
public sealed record EncodeTiming(string Codec, int Frames, double AverageMs, double MaxMs)
{
    /// <summary>A reading older than this is from a stream that has stopped.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(3);

    public static EncodeTiming? Read(string path, DateTimeOffset now)
    {
        try { return Parse(File.ReadAllText(path), now); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public static EncodeTiming? Parse(string text, DateTimeOffset now)
    {
        var values = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
        {
            var at = line.IndexOf('=');
            if (at > 0) values[line[..at].Trim()] = line[(at + 1)..].Trim();
        }
        if (!values.TryGetValue("unix_ms", out var stamp) || !long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixMs)) return null;
        if (now - DateTimeOffset.FromUnixTimeMilliseconds(unixMs) is var age && (age > Fresh || age < -Fresh)) return null;
        if (!values.TryGetValue("codec", out var codec) || codec.Length == 0
            || !values.TryGetValue("frames", out var frames) || !int.TryParse(frames, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count == 0
            || !values.TryGetValue("avg_ms", out var average) || !double.TryParse(average, NumberStyles.Float, CultureInfo.InvariantCulture, out var avg)
            || !values.TryGetValue("max_ms", out var maximum) || !double.TryParse(maximum, NumberStyles.Float, CultureInfo.InvariantCulture, out var max)) return null;
        return new(codec, count, avg, max);
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"encode {AverageMs:0.0} ms avg / {MaxMs:0.0} ms max");
}
