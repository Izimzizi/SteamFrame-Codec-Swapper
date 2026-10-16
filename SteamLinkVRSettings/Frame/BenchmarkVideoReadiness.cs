namespace SteamLinkVRSettings.Frame;

/// <summary>Recover missing output between trials; never combine counters across a reconnect.</summary>
internal sealed class BenchmarkVideoReadiness
{
    private int _recoveries;
    internal const int MaximumRecoveries = 2;

    public async Task EnsureAsync(Func<Task<EncoderSnapshot>> read, Func<CancellationToken, Task> reconnect,
        Action<string> progress, CancellationToken token, Func<CancellationToken, Task>? wait = null)
    {
        wait ??= ct => Task.Delay(500, ct);
        async Task<bool> FreshOutput()
        {
            token.ThrowIfCancellationRequested();
            var previous = await read();
            for (int i = 0; i < 6; i++)
            {
                await wait(token); token.ThrowIfCancellationRequested();
                var current = await read();
                if (FrameReconnectRecovery.FreshVideo(previous, current)) return true;
                previous = current;
            }
            return false;
        }
        if (await FreshOutput()) return;
        if (_recoveries >= MaximumRecoveries)
            throw new IOException("Video stopped repeatedly during auto tune. The two automatic recovery attempts have been used; completed measurements are retained.");
        _recoveries++;
        progress($"Video stopped before measurement. Reconnecting the same PC ({_recoveries} / {MaximumRecoveries} recoveries)…");
        await reconnect(token);
        if (!await FreshOutput())
            throw new IOException("Fresh video did not resume after the recovery attempt. No trial was measured across the reconnect.");
    }
}
