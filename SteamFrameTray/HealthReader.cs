using SteamLinkVRSettings.Frame;

namespace SteamFrameTray;

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
        return new(health?.SenderThrottleEvents ?? 0, health?.ThrottleEventsLastMinute ?? 0, health?.LinkStalls ?? 0,
            composite is { Available: true }, composite?.DroppedFrames ?? 0, composite?.LateRenderFrames ?? 0, composite?.ReprojectedFrames ?? 0);
    }
}
