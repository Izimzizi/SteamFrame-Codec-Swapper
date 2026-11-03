namespace SteamFrame.Core;

/// <summary>
/// Keeps the headset awake for a while by asking the Frame's own Steam UI to extend its activity timeout (the same call Steam UI uses for the HMD).
/// It is a bounded lease, not a power setting: it lapses by itself after the given seconds, and a zero lease ends it at once.
/// </summary>
public static class FrameActivity
{
    public const int LeaseSeconds = 60;

    internal static string Script(int seconds)
    {
        if (seconds is not (0 or LeaseSeconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        const string read = "expression = '({wifi:SystemNetworkStore.isWifiEnabled,links:streamingStore.vrLinkStatus})'";
        if (!FrameLinkQuery.Script.Contains(read)) throw new InvalidOperationException("The Frame UI query changed.");
        return FrameLinkQuery.Script.Replace(read, "expression = '(() => { SteamClient.OpenVR.ExtendActivityTimeout(0, " + seconds + "); return {activityLease: " + seconds + "}; })()'");
    }

    public static async Task SetAsync(FramePreferences preferences, int seconds, CancellationToken token)
    {
        using var response = System.Text.Json.JsonDocument.Parse(await FrameSsh.ExecuteAsync(preferences, Script(seconds), token));
        if (response.RootElement.GetProperty("activityLease").GetInt32() != seconds) throw new IOException("The Frame did not accept the activity extension.");
    }

    /// <summary>Renews the lease every 30 s until disposed, then releases it. A failed renewal is remembered, not thrown: the benchmark reports it if the stream then stops.</summary>
    public sealed class Keeper : IAsyncDisposable
    {
        private readonly FramePreferences _preferences;
        private readonly CancellationTokenSource _stop = new();
        private Task? _loop;
        public string? LastError { get; private set; }

        public Keeper(FramePreferences preferences) => _preferences = preferences;

        /// <summary>Takes the first lease (throws when the Frame cannot be reached) and keeps renewing it.</summary>
        public async Task StartAsync(CancellationToken token)
        {
            await SetAsync(_preferences, LeaseSeconds, token);
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token);
                        try { await SetAsync(_preferences, LeaseSeconds, _stop.Token); LastError = null; }
                        catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException) { LastError = error.Message; }
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_loop is not null) { try { await _loop; } catch (Exception) { } }
            try { await SetAsync(_preferences, 0, CancellationToken.None); }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}
