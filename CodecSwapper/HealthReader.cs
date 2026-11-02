using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>
/// Dropped-frame counters for the stats window and run log: frames the sender held back (from SteamVR's own logs) and
/// the PC compositor's dropped/late frames. Read-only; counts start when the first reading is taken.
/// </summary>
public sealed class HealthReader
{
    private const int CompositorEvery = 5; // the compositor read opens a short OpenVR connection, so do it every few samples
    private readonly Func<StreamingHealth> _createHealth;
    private readonly Func<CompositorTiming> _createCompositor;
    private StreamingHealth? _health;
    private CompositorTiming? _compositor;
    private CompositorTimingSnapshot? _lastCompositor;
    private int _tick;
    // The totals as the logs and the compositor report them, and the values they had when the readout was last reset.
    private long _rawHeld, _rawDropped, _rawLate, _rawReprojected;
    private long _baseHeld, _baseDropped, _baseLate, _baseReprojected;

    /// <summary>Starts the held-back, dropped, late and reprojected totals again from zero. The "last 60 s" figures are not touched.</summary>
    public void ResetCounters()
    {
        (_baseHeld, _baseDropped, _baseLate, _baseReprojected) = (_rawHeld, _rawDropped, _rawLate, _rawReprojected);
    }

    private static long Since(long raw, ref long baseline)
    {
        if (raw < baseline) baseline = 0;   // the source restarted its count
        return raw - baseline;
    }

    public HealthReader(SteamPaths paths, string diagnosticsDirectory)
    {
        _createHealth = () => StreamingHealth.FromSteamRoot(paths.SteamRoot, diagnosticsDirectory);
        _createCompositor = () => new CompositorTiming(paths, diagnosticsDirectory);
    }

    public async Task<HealthReading?> ReadAsync()
    {
        StreamingHealthSnapshot? health = null;
        try { health = await (_health ??= _createHealth()).PollAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { _health = null; }

        if (_tick++ % CompositorEvery == 0 && OpenVrSettings.IsRunning)
        {
            try { _lastCompositor = await (_compositor ??= _createCompositor()).PollAsync(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { _compositor = null; _lastCompositor = null; }
        }
        else if (!OpenVrSettings.IsRunning) _lastCompositor = null;

        if (health is null && _lastCompositor is null) return null;
        var composite = _lastCompositor;
        _rawHeld = health?.SenderThrottleEvents ?? 0;
        _rawDropped = composite?.DroppedFrames ?? 0; _rawLate = composite?.LateRenderFrames ?? 0; _rawReprojected = composite?.ReprojectedFrames ?? 0;
        return new(Since(_rawHeld, ref _baseHeld), health?.ThrottleEventsLastMinute ?? 0, health?.LinkStalls ?? 0,
            composite is { Available: true }, Since(_rawDropped, ref _baseDropped), Since(_rawLate, ref _baseLate), Since(_rawReprojected, ref _baseReprojected));
    }
}
