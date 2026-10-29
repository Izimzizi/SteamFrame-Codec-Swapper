namespace SteamFrame.Core;

// A missing/stale sample is a gap, never a fabricated zero or the requested target.
public sealed class RateTracker
{
    private string? _generation;
    private ulong _bytes;
    private ulong _frames;
    private double _seconds;
    private double _lastFrameAt;
    public (double? Mbps, double? Fps) Sample(string generation, ulong bytes, ulong frames, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) { Reset(); return (null, null); }
        if (_generation != generation || bytes < _bytes || frames < _frames || seconds <= _seconds)
        {
            _generation = generation; _bytes = bytes; _frames = frames; _seconds = seconds; _lastFrameAt = seconds;
            return (null, null);
        }
        var elapsed = seconds - _seconds;
        if (elapsed < 0.1) return (null, null);
        var changed = frames != _frames;
        if (changed) _lastFrameAt = seconds;
        var rate = (bytes - _bytes) * 8.0 / elapsed / 1_000_000.0;
        var fps = (frames - _frames) / elapsed;
        _bytes = bytes; _frames = frames; _seconds = seconds;
        return seconds - _lastFrameAt > 3 ? (null, null) : (rate, fps);
    }
    public void Reset() { _generation = null; }
}
