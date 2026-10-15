using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SteamLinkVRSettings.Frame;

internal sealed class BenchmarkSceneProcess : IAsyncDisposable
{
    private Process? _process;
    private readonly CancellationTokenSource _stop = new();
    private Task? _reader, _heartbeat, _errors;
    private BenchmarkScene? _current;
    private long _updated;
    private bool _disposed;
    public int ProcessId => _process?.Id ?? 0;
    public BenchmarkScene Current => _process is { HasExited: false } && Stopwatch.GetElapsedTime(Interlocked.Read(ref _updated)).TotalSeconds < 2 && _current is { } value
        ? value : throw new IOException("The headset test scene stopped or its measurements are stale.");

    public async Task StartAsync(SteamPaths paths, bool unattended, CancellationToken token)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Native", "SteamFrameBenchmark.exe");
        if (!File.Exists(executable)) throw new IOException("The benchmark scene is missing. Install a complete app build.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(paths.OpenVrApi);
        info.ArgumentList.Add(unattended ? "--unattended" : "--tracked");
        _process = Process.Start(info) ?? throw new IOException("Could not start the benchmark scene.");
        _reader = Task.Run(async () =>
        {
            while (await _process.StandardOutput.ReadLineAsync(_stop.Token) is { } line)
            {
                if (line.Length > 2048) throw new IOException("Invalid scene telemetry.");
                var sample = JsonSerializer.Deserialize<BenchmarkScene>(line) ?? throw new IOException("Missing scene telemetry.");
                if (!double.IsFinite(sample.Seconds) || sample.Seconds < 0 || sample.Frames < 0 || sample.InvalidPoses < 0 || sample.SubmitErrors < 0 || sample.LongIntervals < 0)
                    throw new IOException("Invalid scene counters.");
                _current = sample; Interlocked.Exchange(ref _updated, Stopwatch.GetTimestamp());
            }
        });
        _errors = DrainAsync(_process.StandardError, _stop.Token);
        _heartbeat = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                await _process.StandardInput.WriteLineAsync("heartbeat"); await _process.StandardInput.FlushAsync(_stop.Token);
                await Task.Delay(1000, _stop.Token);
            }
        });
        for (int i = 0; i < 60 && _current is null; i++)
        {
            token.ThrowIfCancellationRequested();
            if (_process.HasExited || _reader.IsFaulted) throw new IOException("SteamVR could not start the benchmark scene. Close the game and keep the Frame awake.");
            await Task.Delay(250, token);
        }
        _ = Current;
    }

    public Task<BenchmarkScene> FreshAsync(CancellationToken token)=>WaitForFreshAsync(()=>Current,token);
    internal static async Task<BenchmarkScene> WaitForFreshAsync(Func<BenchmarkScene> read,CancellationToken token)
    {
        var previous=read().Seconds;
        // Scene telemetry arrives every half second. A short closing sample must
        // not reuse the preceding scene timestamp and look like a stalled counter.
        for(int i=0;i<40;i++)
        {
            await Task.Delay(50,token);var current=read();
            if(current.Seconds>previous)return current;
            if(current.Seconds<previous)throw new IOException("The test scene counters reset.");
        }
        throw new IOException("Fresh test scene measurements did not arrive.");
    }

    public async ValueTask DisposeAsync()
    {
        if(_disposed)return;_disposed=true;
        _stop.Cancel();
        if (_process is not null)
        {
            _process.StandardInput.Close();
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { if (!_process.HasExited) _process.Kill(); }
            foreach (var task in new[] { _reader, _heartbeat, _errors }.OfType<Task>())
                try { await task; } catch (Exception) { /* Process shutdown closes its pipes. */ }
            _process.Dispose();
        }
        _stop.Dispose();
    }
    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    { var buffer = new char[512]; while (await reader.ReadAsync(buffer, token) != 0) { } }
}

internal sealed record BenchmarkHeadset(BenchmarkReceiver Receiver, string Path);

internal static class BenchmarkHeadsetReader
{
    // Reuses strict paired SSH and the read-only active-route query. vrcmd is Valve's
    // installed binary; no root, diagnostic patches, receiver replacement or password storage.
    internal static string Script => FrameLinkQuery.Script.Replace(
        "print(json.dumps(ui.evaluate(expression)))",
        "benchmark_links = ui.evaluate(expression)") + "\n" + BenchmarkWirelessRoute.Python + "\n" + """
import pathlib, subprocess, time
def identity():
    entries=[]
    for p in pathlib.Path('/proc').iterdir():
        if not p.name.isdecimal(): continue
        try:
            comm=(p/'comm').read_text().strip()
            if comm not in ('vrserver','vrlink','vrlinkrunthread'): continue
            stat=(p/'stat').read_text().rsplit(')',1)[1].split()
            entries.append((int(p.name),comm,int(stat[19])))
        except (OSError,IndexError,ValueError): continue
    if not any(x[1] in ('vrlink','vrlinkrunthread') for x in entries): raise RuntimeError('Receiver not running')
    return json.dumps(sorted(entries)) + pathlib.Path('/proc/sys/kernel/random/boot_id').read_text().strip()
before=identity()
values=json.loads(subprocess.run(['/opt/steamvr/bin/linuxarm64/vrcmd','--stats'],capture_output=True,text=True,check=True,timeout=4).stdout)
stats=[x for x in values if x.get('key')=='vrlink.client' and x.get('operation')=='compositor_stats']
if len(stats)!=1 or identity()!=before: raise RuntimeError('Receiver statistics changed')
s=stats[0]
links=benchmark_links['links']
route=benchmark_wireless_route(links)
print(json.dumps(dict(Receiver=dict(Seconds=time.monotonic(),Identity=before,
 Submits=s['frame_submits'],Presents=s['frame_presents'],Drops=s['dropped_frames'],Timeouts=s['frame_presents_timed_out']),
 Path=route)))
""";

    public static async Task<BenchmarkHeadset> ReadAsync(FramePreferences preferences, CancellationToken token)
        => Parse(await FrameSsh.ExecuteAsync(preferences, Script, token));

    internal static string ActivityScript(int seconds)
    {
        if (seconds is not (0 or 60)) throw new ArgumentOutOfRangeException(nameof(seconds));
        // This is Steam UI's own bounded HMD activity extension, not a permanent power setting.
        return FrameLinkQuery.Script.Replace("expression = '({wifi:SystemNetworkStore.isWifiEnabled,links:streamingStore.vrLinkStatus})'",
            "expression = '(() => { SteamClient.OpenVR.ExtendActivityTimeout(0, " + seconds + "); return {activityLease: " + seconds + "}; })()'");
    }

    public static async Task SetActivityAsync(FramePreferences preferences, int seconds, CancellationToken token)
    {
        using var response = JsonDocument.Parse(await FrameSsh.ExecuteAsync(preferences, ActivityScript(seconds), token));
        if (response.RootElement.GetProperty("activityLease").GetInt32() != seconds) throw new IOException("Frame activity extension was not accepted.");
    }

    internal static BenchmarkHeadset Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var receiver = document.RootElement.GetProperty("Receiver");
        foreach (var field in new[] { "Seconds", "Identity", "Submits", "Presents", "Drops", "Timeouts" })
            if (!receiver.TryGetProperty(field, out _)) throw new IOException("Incomplete headset counters: " + field);
        var result = JsonSerializer.Deserialize<BenchmarkHeadset>(json) ?? throw new IOException("Missing headset measurements.");
        var r = result.Receiver;
        if (r is null || !double.IsFinite(r.Seconds) || r.Seconds <= 0 || string.IsNullOrWhiteSpace(r.Identity) ||
            r.Submits < 0 || r.Presents < 0 || r.Drops < 0 || r.Timeouts < 0 || result.Path is not ("Dedicated wireless" or "Local network"))
            throw new IOException("Unsupported headset measurements.");
        return result;
    }

}

internal sealed class BenchmarkActivityLease(FramePreferences preferences) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _renewal;
    private bool _requested;
    public async Task StartAsync(CancellationToken token)
    {
        _requested = true;
        await BenchmarkHeadsetReader.SetActivityAsync(preferences, 60, token);
        _renewal = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(20000, _stop.Token);
                await BenchmarkHeadsetReader.SetActivityAsync(preferences, 60, _stop.Token);
            }
        });
    }
    public void Check()
    {
        if (_renewal?.IsFaulted == true) throw new IOException("The Frame's temporary activity extension failed. Starting settings will be restored.");
    }
    // Called only after the persistent monitor has acknowledged its first
    // activity renewal. Cleanup remains owned here, including failed preflights.
    public async Task HandOffToMonitorAsync()
    {
        Check(); _stop.Cancel();
        if (_renewal is not null)
            try { await _renewal; } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        Check();
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_renewal is not null) try { await _renewal; } catch (Exception) { }
        if (_requested)
            try { await BenchmarkHeadsetReader.SetActivityAsync(preferences, 0, CancellationToken.None); }
            catch (Exception) { /* A lost connection leaves at most the existing 60-second extension. */ }
        _stop.Dispose();
    }
}
