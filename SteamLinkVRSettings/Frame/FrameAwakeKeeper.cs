namespace SteamLinkVRSettings.Frame;

/// <summary>
/// Keeps the Frame streaming while nothing is on its proximity sensor by renewing the headset's own
/// bounded activity extension (60 s) every 20 s. Disposing stops the renewals and ends the extension,
/// so a crashed caller leaves at most one minute of extra activity. The extension call is injected so
/// the schedule can be tested without a headset.
/// </summary>
public sealed class FrameAwakeKeeper : IAsyncDisposable
{
    public static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(20);
    public const int LeaseSeconds = 60;
    private readonly Func<int, CancellationToken, Task> _extend;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _started, _disposed;
    private int _renewals;
    private Exception? _lastError;

    public FrameAwakeKeeper(Func<int, CancellationToken, Task> extend, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _extend = extend ?? throw new ArgumentNullException(nameof(extend));
        _delay = delay ?? ((wait, token) => Task.Delay(wait, token));
    }

    /// <summary>Renewals accepted so far, including the first.</summary>
    public int Renewals => Volatile.Read(ref _renewals);
    /// <summary>The most recent renewal failure, cleared by the next success. The keeper keeps retrying.</summary>
    public Exception? LastError => Volatile.Read(ref _lastError);
    public bool Active => _started && !_disposed;

    /// <summary>Requests the first extension now; a failure here propagates so the caller can say so.</summary>
    public async Task StartAsync(CancellationToken token)
    {
        if (_started) return;
        await _extend(LeaseSeconds, token);
        _started = true;
        Interlocked.Exchange(ref _renewals, 1);
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await _delay(RenewEvery, _stop.Token);
                    await _extend(LeaseSeconds, _stop.Token);
                    Interlocked.Increment(ref _renewals);
                    Volatile.Write(ref _lastError, null);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception error) { Volatile.Write(ref _lastError, error); }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (_loop is not null) { try { await _loop; } catch (Exception) { } }
        if (_started)
        {
            // End the extension now rather than letting the last minute run out.
            try { await _extend(0, CancellationToken.None); } catch (Exception) { }
        }
        _stop.Dispose();
    }

    /// <summary>The real thing: the headset's Steam UI activity extension over the paired SSH link.</summary>
    public static FrameAwakeKeeper ForFrame(FramePreferences preferences)
        => new((seconds, token) => BenchmarkHeadsetReader.SetActivityAsync(preferences, seconds, token));
}
