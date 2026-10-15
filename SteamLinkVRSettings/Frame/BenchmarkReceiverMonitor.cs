using System.Diagnostics;
using System.Text.Json;

namespace SteamLinkVRSettings.Frame;

/// <summary>One owned SSH/OpenVR connection; no repeated vrcmd processes during a trial.</summary>
internal sealed class BenchmarkReceiverMonitor : IAsyncDisposable
{
    private Process? _process;
    private readonly CancellationTokenSource _stop = new();
    private Task? _reader, _errors;
    private readonly object _gate = new();
    private BenchmarkHeadset? _current;
    private long _at;
    private string _errorTail="";
    private bool _disposed;
    public BenchmarkHeadset Current
    {
        get
        {
            lock (_gate)
                return _process is { HasExited: false } && _reader?.IsFaulted != true && _current is not null &&
                    Stopwatch.GetElapsedTime(_at).TotalSeconds < 6 ? _current : throw Failure("The headset counter monitor stopped or became stale.");
        }
    }
    public async Task StartAsync(FramePreferences preferences, bool keepAwake, CancellationToken token,Action<string>? waiting=null)
    {
        _process = Process.Start(FrameReceiverDiagnostics.CreateSshStartInfo(preferences)) ?? throw new IOException("Could not start headset counter monitoring.");
        _reader = Task.Run(async () =>
        {
            while (await _process.StandardOutput.ReadLineAsync(_stop.Token) is { } line)
            {
                if (line.Length > 8192) throw new IOException("Headset telemetry exceeded its metadata limit.");
                var sample=BenchmarkHeadsetReader.Parse(line);
                lock (_gate) { _current=sample; _at=Stopwatch.GetTimestamp(); }
            }
        });
        _errors = Task.Run(async () =>
        {
            var buffer=new char[512];int count;
            while ((count=await _process.StandardError.ReadAsync(buffer,_stop.Token))!=0)
                lock(_gate){_errorTail+=new string(buffer,0,count);if(_errorTail.Length>2048)_errorTail=_errorTail[^2048..];}
        });
        var script=Script.Replace("keep_awake=False",keepAwake ? "keep_awake=True" : "keep_awake=False");
        await _process.StandardInput.WriteAsync(script.AsMemory(),token); _process.StandardInput.Close();
        for(int i=0;i<300;i++)
        {
            token.ThrowIfCancellationRequested();
            lock(_gate) if(_current is not null) return;
            if(_process.HasExited || _reader.IsFaulted)
            {
                if(_process.HasExited && _errors is not null)await _errors.WaitAsync(TimeSpan.FromSeconds(2),token);
                throw StartupFailure();
            }
            if(i%10==0)waiting?.Invoke($"Waiting for headset measurements and a wireless route ({i/10} / 30 seconds)…");
            await Task.Delay(100,token);
        }
        throw StartupFailure();
    }
    private BenchmarkReceiverStartupException StartupFailure()
    {
        var message=Failure("The headset's measurements could not become ready.").Message;
        bool retryable=message.Contains("BenchmarkRouteNotReady:",StringComparison.Ordinal) ||
            message.Contains("Receiver not running",StringComparison.Ordinal) || message.Contains("Receiver scene changed",StringComparison.Ordinal) ||
            message.Contains("streaming scene lost focus",StringComparison.Ordinal);
        return new(message,retryable);
    }
    private IOException Failure(string message)
    {
        lock(_gate)
        {
            var reason=_reader?.Exception?.GetBaseException().Message ?? _errorTail.Trim().Split('\n').LastOrDefault();
            return new IOException(message+(string.IsNullOrWhiteSpace(reason)?"":" "+reason));
        }
    }
    public async Task<BenchmarkHeadset> FreshAsync(CancellationToken token)
    {
        var previous=Current.Receiver.Seconds;
        for(int i=0;i<60;i++)
        {
            await Task.Delay(100,token);
            var value=Current;
            if(value.Receiver.Seconds>previous)return value;
        }
        throw new IOException("Fresh headset counters did not arrive.");
    }
    public async ValueTask DisposeAsync()
    {
        if(_disposed)return;_disposed=true;
        _stop.Cancel();
        if(_process is not null)
        {
            if(!_process.HasExited)_process.Kill(entireProcessTree:true);
            foreach(var task in new[]{_reader,_errors}.OfType<Task>())try{await task;}catch(Exception){}
            _process.Dispose();
        }
        _stop.Dispose();
    }
    internal static string Script => FrameLinkQuery.SessionScript + "\n" + BenchmarkWirelessRoute.Python + "\n" + """
import ctypes as c,json,pathlib,time
keep_awake=False
# This observer is expendable background work; it must not compete with streaming.
try:os.nice(10)
except OSError:pass
lib=c.CDLL('/opt/steamvr/bin/linuxarm64/libopenvr_api.so')
lib.VR_InitInternal2.argtypes=[c.POINTER(c.c_int),c.c_int,c.c_char_p];lib.VR_InitInternal2.restype=c.c_uint32
lib.VR_GetGenericInterface.argtypes=[c.c_char_p,c.POINTER(c.c_int)];lib.VR_GetGenericInterface.restype=c.c_void_p
error=c.c_int();lib.VR_InitInternal2(c.byref(error),3,None)
if error.value:raise RuntimeError('Frame OpenVR connection failed')
watched=None
def identity():
    global watched
    entries=[]
    # Discover once, then verify the same PIDs/start times without rescanning all processes.
    paths=pathlib.Path('/proc').iterdir() if watched is None else (pathlib.Path('/proc')/str(pid) for pid in watched)
    for p in paths:
        if not p.name.isdecimal():continue
        try:
            comm=(p/'comm').read_text().strip()
            if comm not in ('vrserver','vrlink','vrlinkrunthread'):continue
            entries.append((int(p.name),comm,int((p/'stat').read_text().rsplit(')',1)[1].split()[19])))
        except (OSError,IndexError,ValueError):continue
    if not any(x[1] in ('vrlink','vrlinkrunthread') for x in entries):raise RuntimeError('Receiver not running')
    if watched is None:watched=[x[0] for x in entries]
    return json.dumps(sorted(entries))+pathlib.Path('/proc/sys/kernel/random/boot_id').read_text().strip()
try:
    p=lib.VR_GetGenericInterface(b'FnTable:IVRCompositor_029',c.byref(error))
    if not p or error.value:raise RuntimeError('Frame compositor counters unavailable')
    table=c.cast(p,c.POINTER(c.c_void_p))
    stats=c.CFUNCTYPE(None,c.c_void_p,c.c_uint32)(table[13]);scene=c.CFUNCTYPE(c.c_uint32)(table[24])
    buffer=c.create_string_buffer(112)
    started=time.monotonic();initial=None;renew_at=0;ready_key=None;ready_count=0
    with FrameUiConnection() as ui:
      while time.monotonic()-started<7200:
        if keep_awake and time.monotonic()>=renew_at:
            accepted=ui.evaluate('(() => { SteamClient.OpenVR.ExtendActivityTimeout(0, 60); return 60; })()')
            if accepted!=60:raise RuntimeError('Activity extension was not accepted')
            renew_at=time.monotonic()+20
        try:
            links=ui.evaluate('streamingStore.vrLinkStatus');route=benchmark_wireless_route(links)
            before=scene();stats(buffer,112);stamp=time.monotonic();s=list(c.cast(buffer,c.POINTER(c.c_uint32))[:16])
            current_identity=identity()
            if not before or s[0]!=before or scene()!=before or initial is not None and current_identity!=initial:raise RuntimeError('Receiver scene changed')
            if (pathlib.Path('/proc')/str(before)/'comm').read_text().strip() not in ('vrlink','vrlinkrunthread'):raise RuntimeError('The streaming scene lost focus')
        except (RuntimeError,OSError,KeyError):
            # Startup is not a measurement window. Give a newly resumed receiver
            # time to acquire focus; never retry/reset identities after sampling.
            if initial is not None or time.monotonic()-started>=20:raise
            watched=None;ready_key=None;ready_count=0;time.sleep(.2);continue
        if initial is None:
            key=(current_identity,before,route)
            ready_count=ready_count+1 if key==ready_key else 1;ready_key=key
            if ready_count<3:time.sleep(.25);continue
        initial=current_identity
        print(json.dumps(dict(Receiver=dict(Seconds=stamp,Identity=initial+'-'+str(before),
            Submits=s[15],Presents=s[1],Drops=s[2],Timeouts=s[12]),
            Path=route)),flush=True)
        time.sleep(1)
finally:lib.VR_ShutdownInternal()
""";
}
