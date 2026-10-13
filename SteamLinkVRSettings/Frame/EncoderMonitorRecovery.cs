namespace SteamLinkVRSettings.Frame;

public sealed record EncoderMonitorStartResult(EncoderSnapshot Snapshot, bool Restarted);

/// <summary>Explicit user actions only. Polling must never call this restart coordinator.</summary>
public static class EncoderMonitorRecovery
{
    public static async Task<EncoderMonitorStartResult> StartAsync(
        Func<Task<EncoderSnapshot>> start, Func<Task<EncoderSnapshot>> read,
        Func<int, Task> restart, Action<string> progress, CancellationToken token = default,
        int attempts = 120, Func<CancellationToken, Task>? wait = null)
    {
        token.ThrowIfCancellationRequested();
        var before = await start();
        if (!before.RequiresSteamVrRestart || before.PairedCodecTrialPresent)
            return new(before, false);
        if (before.ProcessId <= 0) throw new InvalidOperationException("The SteamVR session changed. Try Start monitoring again.");
        token.ThrowIfCancellationRequested();
        progress("Restarting SteamVR to finish the app update. The headset will disconnect briefly…");
        await restart(before.ProcessId);
        progress("SteamVR is restarting. Waiting for its streaming driver, then resuming monitoring…");
        wait ??= cancellation => Task.Delay(500, cancellation);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var current = await read();
            // Never install into the old process, and never take over a paired codec session.
            if (current.ProcessId > 0 && current.ProcessId != before.ProcessId)
            {
                if (current.PairedCodecTrialPresent || current.RequiresSteamVrRestart)
                    return new(current, true);
                if (!current.WaitingForDriver)
                    return new(current.Supported ? await start() : current, true);
            }
            await wait(token);
        }
        throw new TimeoutException("SteamVR restarted, but its streaming driver is not ready. Reconnect the Frame, then select Start monitoring. The app will not restart SteamVR again automatically.");
    }
}
