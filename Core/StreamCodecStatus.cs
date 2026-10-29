namespace SteamFrame.Core;

/// <summary>Display only fresh, verified encoder evidence as a running codec.</summary>
public sealed record StreamCodecStatus(string Value, string Detail, bool Verified)
{
    public static string Normalize(string? codec) => codec?.Trim().ToUpperInvariant() switch
    {
        "HEVC" or "H.265" => "HEVC",
        "AV1" => "AV1",
        "H.264" or "H264" => "H.264",
        _ => "Unknown"
    };

    public static StreamCodecStatus From(EncoderSnapshot snapshot)
    {
        var codec = Normalize(snapshot.Codec);
        if (snapshot.ProcessId == 0) return new("Unavailable", "SteamVR is not running.", false);
        if (snapshot.CodecReadFailed) return new("Unknown", "Current encoder evidence is unavailable.", false);
        if (!snapshot.Installed) return new("Unknown", snapshot.CodecObserved && codec != "Unknown"
            ? $"Monitoring stopped. Last verified codec: {codec}."
            : "Start the monitor to verify the active encoder.", false);
        if (!snapshot.Active) return new("Waiting", snapshot.CodecObserved && codec != "Unknown"
            ? $"No recent encoded output. Last verified codec: {codec}."
            : "Waiting for fresh encoded output; codec is not yet confirmed.", false);
        if (!snapshot.CodecObserved || codec == "Unknown")
            return new("Unknown", "Video output detected; codec metadata is unavailable.", false);
        var depth = snapshot.BitDepth is uint bits ? $" • {bits}-bit" : "";
        return new(codec, (string.IsNullOrEmpty(snapshot.CodecSource)
            ? "Verified from encoder output" : snapshot.CodecSource) + depth, true);
    }
}

/// <summary>A cumulative first sample cannot prove output is still advancing.</summary>
public sealed class EncoderOutputActivity
{
    private string? _identity;
    private ulong _frames;
    private double _lastOutput;
    private uint _epoch;

    public (bool Active, double LastOutput, string Generation) Sample(string identity, ulong frames, double now)
    {
        if (_identity != identity || frames < _frames)
        {
            _epoch = _identity == identity ? _epoch + 1 : 0;
            _identity = identity;
            _frames = frames;
            _lastOutput = 0;
        }
        else
        {
            if (frames > _frames) _lastOutput = now;
            _frames = frames;
        }
        return (_lastOutput > 0 && now >= _lastOutput && now - _lastOutput < 2,
            _lastOutput, $"{identity}-{_epoch}");
    }

    public void Reset() { _identity = null; _frames = 0; _lastOutput = 0; _epoch = 0; }
}
