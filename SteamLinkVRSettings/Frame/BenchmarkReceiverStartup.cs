namespace SteamLinkVRSettings.Frame;

internal sealed class BenchmarkReceiverStartupException(string message,bool retryable) : IOException(message)
{
    public bool Retryable { get; }=retryable;
}

internal static class BenchmarkReceiverStartup
{
    // Only reconnect before measurement starts. Never join counter windows over
    // a disconnect, or turn a real mid-trial failure into a clean measurement.
    public static async Task<T> ConnectAsync<T>(Func<CancellationToken,Task<T>> connect,
        Func<CancellationToken,Task> resumeSameHost,Action<string> progress,CancellationToken token)
    {
        try {return await connect(token);}
        catch(BenchmarkReceiverStartupException error) when(error.Retryable)
        {
            token.ThrowIfCancellationRequested();
            progress("The wireless stream is still reconnecting after the mode change. Resuming the same PC and retrying headset measurements once…");
            await resumeSameHost(token);
            try {return await connect(token);}
            catch(BenchmarkReceiverStartupException retry)
            {throw new IOException("The headset measurements did not recover after one reconnect attempt. "+retry.Message,retry);}
        }
    }
}

internal static class BenchmarkWirelessRoute
{
    // Shared by preflight and the persistent observer. Aggregate type 255 is
    // not a second route. Ambiguous or wired paths remain invalid evidence.
    internal const string Python="""
class BenchmarkRouteNotReady(RuntimeError):pass
def benchmark_wireless_route(links):
    if not links.get('bHasInfo',False):raise BenchmarkRouteNotReady('Headset link status is not ready')
    active=[x for x in links.get('links',[]) if x.get('bActive',False) and x.get('type')!=255]
    if not active:raise BenchmarkRouteNotReady('No active wireless transport yet')
    if len(active)!=1:raise RuntimeError('Multiple active transports: '+','.join(str(x.get('type')) for x in active))
    kind=active[0].get('type')
    if kind not in (2,3):raise RuntimeError('Wireless benchmark cannot use active transport type '+str(kind))
    return 'Dedicated wireless' if kind==2 else 'Local network'
""";
}
