namespace SteamLinkVRSettings.Frame;

/// <summary>One bounded runtime restart when NVENC cannot change rate control in place.</summary>
internal static class BenchmarkRuntimeRestart
{
    public static async Task<EncoderSnapshot> RunAsync(SteamPaths paths, FramePreferences preferences, EncoderProbe probe,
        BenchmarkSettingsLease lease, Action<string> progress, CancellationToken token,string? capturedHost=null)
    {
        await lease.CheckControlsAsync();
        var host=capturedHost??await BenchmarkHostConnection.CaptureAsync(preferences,token);
        FrameReconnectTuning? expected=null;
        var result=await FrameReconnectRecovery.RunAsync(probe.GetSnapshotAsync,
            pid=>SteamVrRuntime.StartOrRestartForReconnectAsync(paths,pid),async(before,ready)=>
            {
                expected=new(before.Enabled,before.CenterOffset,before.PeripheralOffset,before.EncodingPolicy!.Requested,
                    before.AdvancedEncoding?.Requested ?? new());
                var current=await probe.StartAsync();
                // Record the replacement helper before writing any requests into it.
                await lease.AdoptRestartAsync(before,current);
                await probe.SetQualityAsync(before.Enabled,before.CenterOffset,before.PeripheralOffset);
                await probe.SetEncodingPolicyAsync(before.EncodingPolicy!.Requested);
                current=await probe.SetAdvancedEncodingAsync(before.AdvancedEncoding?.Requested ?? new());
                await BenchmarkHostConnection.ResumeAsync(preferences,host,token);
                return current;
            },progress,token);
        var snapshot=result.Snapshot;
        if(!result.VideoResumed || expected?.RequestsRestored(snapshot)!=true ||
            snapshot.AdvancedEncoding is {PendingFlags:not 0} or {RejectedFlags:not 0} ||
            snapshot.EncodingPolicy is not {Observed:true,Pending:false,NeedsNewSession:false,Unsupported:false,InitializationRejected:false})
            throw new IOException($"The requested encoder mode did not activate after one SteamVR restart (fresh video: {result.VideoResumed}; {snapshot.EncodingPolicy?.StatusMessage ?? snapshot.Message}). Starting-settings recovery remains available.");
        await OpenVrSettings.WaitUntilAvailableAsync(paths);
        await lease.CheckControlsAsync();
        return snapshot;
    }
}

internal static class BenchmarkHostConnection
{
    public static async Task<string> CaptureAsync(FramePreferences preferences,CancellationToken token)
    {
        const string expression="""
(() => {
 const streams=RemotePlayStore_SteamUI.remoteClientStreams.filter(s=>String(s.m_gameid)==="250820");
 if(streams.length!==1)throw new Error("One active VR host is required");
 const host=String(streams[0].m_clientid);
 const matches=RemotePlayStore_SteamUI.devices.filter(d=>String(d.clientId)===host && d.bCanStreamVR && d.bHasLocalConnection);
 if(matches.length!==1)throw new Error("The current VR host is unavailable");return host;
})()
""";
        return System.Text.Json.JsonSerializer.Deserialize<string>(await Evaluate(preferences,expression,token))
            ?? throw new IOException("The current VR host was not identified.");
    }
    public static async Task ResumeAsync(FramePreferences preferences,string host,CancellationToken token)
    {
        string identity=System.Text.Json.JsonSerializer.Serialize(host);
        string expression="""
(() => {
 const streams=RemotePlayStore_SteamUI.remoteClientStreams.filter(s=>String(s.m_gameid)==="250820");
 if(streams.some(s=>String(s.m_clientid)!==HOST))throw new Error("Another VR host is active");
 const hosts=RemotePlayStore_SteamUI.devices.filter(d=>String(d.clientId)===HOST && d.bCanStreamVR && d.bHasLocalConnection);
 if(hosts.length!==1)throw new Error("The same VR host is no longer available");
 SteamClient.Apps.StreamGame(250820,hosts[0].clientId,-1);return true;
})()
""".Replace("HOST",identity);
        if(!System.Text.Json.JsonSerializer.Deserialize<bool>(await Evaluate(preferences,expression,token)))
            throw new IOException("The Frame did not resume the selected VR host.");
    }
    private static Task<string> Evaluate(FramePreferences preferences,string expression,CancellationToken token) =>
        FrameSsh.ExecuteAsync(preferences,FrameLinkQuery.SessionScript+"\nwith FrameUiConnection() as ui:\n print(json.dumps(ui.evaluate("+
            System.Text.Json.JsonSerializer.Serialize(expression)+")))",token);
}

internal sealed class BenchmarkOpenVrConnection(SteamPaths paths) : IDisposable
{
    private IDisposable? _connection=OpenVrSettings.RetainConnection(paths);
    public void Release() { _connection?.Dispose();_connection=null; }
    public void Retain() { _connection ??= OpenVrSettings.RetainConnection(paths); }
    public void Dispose() => Release();
}
