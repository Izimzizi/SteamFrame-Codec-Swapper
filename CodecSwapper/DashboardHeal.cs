using System.Text.Json;

namespace CodecSwapper;

/// <summary>
/// After a codec switch the Frame's dashboard sometimes has the stream but none of the PC's windows (the desktop and the Steam window reach it as frames
/// from the PC, and the PC does not always send them after the new stream starts). A second after a switch finishes the Frame is asked what its dashboard
/// holds (again every second until it has a stream); when it has a stream without any PC frame, and still has none a second later, the windows are brought back the way the dashboard's + does it (see <see cref="DashboardNudge"/>; a stream
/// restart and a Frame client restart were tried and do not help), then it is checked again. Every decision is written to the dashboard watch log.
/// </summary>
public sealed class DashboardHeal(Func<string, Task<string>> evaluate, Func<Task<int>> nudge, Action failed, Action<string> log, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan First = TimeSpan.FromSeconds(1), Poll = TimeSpan.FromSeconds(1), Settle = TimeSpan.FromSeconds(1), Recheck = TimeSpan.FromSeconds(3), GiveUp = TimeSpan.FromSeconds(15);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((time, token) => Task.Delay(time, token));

    /// <summary>The number of running streams and of dashboard frames that come from the PC, from the Frame's reply; null when it cannot be read.</summary>
    public static (int Streams, int Remote)? Parse(string reply)
    {
        try
        {
            var text = JsonSerializer.Deserialize<string>(reply) ?? reply;
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) return null;
            var remote = root.TryGetProperty("frames", out var frames) && frames.ValueKind == JsonValueKind.Array
                ? frames.EnumerateArray().Count(f => f.TryGetProperty("remote", out var flag) && flag.ValueKind == JsonValueKind.Number && flag.GetInt32() == 1) : 0;
            return (streams.GetArrayLength(), remote);
        }
        catch (JsonException) { return null; }
    }

    private async Task<(int Streams, int Remote)?> ReadAsync()
    {
        try { return Parse(await evaluate(DashboardWatch.FrameExpression)); }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or FileNotFoundException or UnauthorizedAccessException) { log("HEAL the Frame could not be asked: " + error.Message); return null; }
    }

    public async Task RunAsync(CancellationToken token)
    {
        // The dashboard is empty for as long as this takes, so it looks early and often instead of waiting once: the stream may not be up yet, and the
        // windows normally follow the stream within a second or two.
        await _delay(First, token);
        var waited = First;
        (int Streams, int Remote) state;
        while (true)
        {
            if (await ReadAsync() is not { } read) return;
            state = read;
            if (state.Remote > 0) { log($"HEAL the Frame has {state.Remote} PC frame(s); nothing to do"); return; }
            if (state.Streams > 0) break;
            if (waited >= GiveUp) { log("HEAL no stream on the Frame after the switch; nothing to do"); return; }
            await _delay(Poll, token);
            waited += Poll;
        }
        await _delay(Settle, token);
        if (await ReadAsync() is not { } confirm) return;
        if (confirm.Remote > 0) { log($"HEAL the Frame has {confirm.Remote} PC frame(s); nothing to do"); return; }
        log("HEAL the Frame has a stream but no PC frames (the desktop and Steam windows); asking the PC's dashboard to send them");
        var code = await nudge();
        log("HEAL nudge finished with code " + code);
        await _delay(Recheck, token);
        if (await ReadAsync() is not { } second) return;
        if (second.Remote > 0) { log($"HEAL worked: the Frame now has {second.Remote} PC frame(s)"); return; }
        log($"HEAL did not help: still no PC frames (streams {second.Streams})");
        failed();
    }
}
