using System.Text.Json;

namespace CodecSwapper;

/// <summary>
/// After a codec switch the Frame's dashboard sometimes has the stream but none of the PC's windows (the desktop and the Steam window reach it as frames
/// from the PC, and the PC does not always send them after the new stream starts). Ten seconds after a switch the Frame is asked what its dashboard holds,
/// and when it has a stream without any PC frame the user is told (the tray's "Restore dashboard windows" brings them back). A plain stream restart does
/// not, so nothing is restarted here. Every decision is written to the dashboard watch log.
/// </summary>
public sealed class DashboardHeal(Func<string, Task<string>> evaluate, Action missing, Action<string> log, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
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
        await _delay(Wait, token);
        if (await ReadAsync() is not { } first) return;
        if (first.Streams == 0) { log("HEAL no stream on the Frame ten seconds after the switch; nothing to do"); return; }
        if (first.Remote > 0) { log($"HEAL the Frame has {first.Remote} PC frame(s); nothing to do"); return; }
        log("HEAL the Frame has a stream but no PC frames (the desktop and Steam windows); telling the user to restore them");
        missing();
    }
}
