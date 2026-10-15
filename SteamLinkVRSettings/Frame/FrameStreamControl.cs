namespace SteamLinkVRSettings.Frame;

/// <summary>
/// Stops and starts the Frame's PC VR stream from the headset's own Steam client, through the paired
/// SSH link and the same UI calls the headset's menu uses. This is the "stop streaming, then stream
/// again" fix without restarting SteamVR. Never invoked by polling; only by explicit actions and by
/// the capacity sweep when its link drops.
/// </summary>
internal static class FrameStreamControl
{
    public const string SteamVrAppId = "250820";
    public static readonly TimeSpan InactiveWait = TimeSpan.FromSeconds(15), ActiveWait = TimeSpan.FromSeconds(75), Settle = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The PC the Frame should stream from: the host of its current VR stream, or the only
    /// VR-capable local host it knows. Several candidates without an active stream are refused.
    /// </summary>
    public static async Task<string> FindHostAsync(FramePreferences preferences, CancellationToken token)
    {
        const string expression = """
(() => {
 const streams = RemotePlayStore_SteamUI.remoteClientStreams.filter(s => String(s.m_gameid) === "250820").map(s => String(s.m_clientid));
 const hosts = RemotePlayStore_SteamUI.devices.filter(d => d.bCanStreamVR && d.bHasLocalConnection).map(d => String(d.clientId));
 const streaming = hosts.filter(h => streams.includes(h));
 const chosen = streaming.length === 1 ? streaming[0] : streaming.length === 0 && hosts.length === 1 ? hosts[0] : null;
 if (!chosen) throw new Error(hosts.length === 0 ? "The Frame sees no PC that can stream VR." : "The Frame sees several PCs that can stream VR; start the stream from the Frame once so the app knows which one.");
 return chosen;
})()
""";
        return System.Text.Json.JsonSerializer.Deserialize<string>(await Evaluate(preferences, expression, token))
            ?? throw new IOException("The Frame did not identify a VR host.");
    }

    /// <summary>Ends the headset's streaming client for SteamVR, as the Frame's own Stop streaming action does.</summary>
    public static async Task StopAsync(FramePreferences preferences, CancellationToken token)
    {
        const string expression = """
(() => { SteamClient.Apps.TerminateApp("250820", false); return true; })()
""";
        if (!System.Text.Json.JsonSerializer.Deserialize<bool>(await Evaluate(preferences, expression, token)))
            throw new IOException("The Frame did not accept the stop request.");
    }

    /// <summary>Starts (or resumes) the PC VR stream from the headset to the given host.</summary>
    public static async Task StartAsync(FramePreferences preferences, string host, CancellationToken token)
    {
        var identity = System.Text.Json.JsonSerializer.Serialize(host);
        var expression = """
(() => {
 const streams = RemotePlayStore_SteamUI.remoteClientStreams.filter(s => String(s.m_gameid) === "250820");
 if (streams.some(s => String(s.m_clientid) !== HOST)) throw new Error("Another VR host is active");
 const hosts = RemotePlayStore_SteamUI.devices.filter(d => String(d.clientId) === HOST && d.bCanStreamVR && d.bHasLocalConnection);
 if (hosts.length !== 1) throw new Error("The VR host is no longer available to the Frame");
 SteamClient.Apps.StreamGame(250820, hosts[0].clientId, -1); return true;
})()
""".Replace("HOST", identity);
        if (!System.Text.Json.JsonSerializer.Deserialize<bool>(await Evaluate(preferences, expression, token)))
            throw new IOException("The Frame did not start the VR stream.");
    }

    /// <summary>
    /// Stop, wait for the PC driver to report the link inactive, start, and wait for it to report
    /// active again. Uses the driver log collector so no encoder monitor is required.
    /// Returns false when the link did not come back within the wait.
    /// </summary>
    public static async Task<bool> RestartAsync(FramePreferences preferences, StreamingHealth health, Action<string> progress, CancellationToken token)
    {
        var host = await FindHostAsync(preferences, token);
        var before = await health.PollAsync(token);
        progress("Stopping the Frame's PC stream…");
        await StopAsync(preferences, token);
        await WaitForLinkAsync(health, false, before.LinkChangedAt, InactiveWait, token);
        await Task.Delay(Settle, token);
        progress("Starting the PC stream from the Frame…");
        var marker = await health.PollAsync(token);
        await StartAsync(preferences, host, token);
        return await WaitForLinkAsync(health, true, marker.LinkChangedAt, ActiveWait, token);
    }

    /// <summary>Starts the stream without stopping first, for a link that is already down.</summary>
    public static async Task<bool> StartAndWaitAsync(FramePreferences preferences, StreamingHealth health, Action<string> progress, CancellationToken token)
    {
        var host = await FindHostAsync(preferences, token);
        var marker = await health.PollAsync(token);
        progress("Starting the PC stream from the Frame…");
        await StartAsync(preferences, host, token);
        return await WaitForLinkAsync(health, true, marker.LinkChangedAt, ActiveWait, token);
    }

    private static async Task<bool> WaitForLinkAsync(StreamingHealth health, bool active, DateTimeOffset? since, TimeSpan limit, CancellationToken token)
    {
        var deadline = DateTimeOffset.Now + limit;
        while (DateTimeOffset.Now < deadline)
        {
            await Task.Delay(1000, token);
            var current = await health.PollAsync(token);
            if (current.LinkActive == active && current.LinkChangedAt is { } changed && (since is null || changed > since)) return true;
        }
        return false;
    }

    private static Task<string> Evaluate(FramePreferences preferences, string expression, CancellationToken token) =>
        FrameSsh.ExecuteAsync(preferences, FrameLinkQuery.SessionScript + "\nwith FrameUiConnection() as ui:\n print(json.dumps(ui.evaluate(" +
            System.Text.Json.JsonSerializer.Serialize(expression) + ")))", token);
}
