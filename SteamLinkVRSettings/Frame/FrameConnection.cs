using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLinkVRSettings.Frame;

public sealed class FrameConnection : IAsyncDisposable
{
    private readonly FramePreferences _preferences;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private int _port;
    private string _serial = "";
    private string _adb = "";
    private string _requestedSerial = "";
    public FrameConnection(FramePreferences preferences) => _preferences = preferences;

    public async Task<ConnectionSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_preferences.SshHost))
            return await ReadWirelessAsync(cancellationToken);
        try
        {
            if (!File.Exists(_preferences.AdbPath)) return new(null, false, [], "Set the ADB path under Connection setup to read the headset's active links.");
            if (_port == 0 || _adb != _preferences.AdbPath || _requestedSerial != _preferences.AdbSerial)
            {
                await RemoveForwardAsync();
                _adb = _preferences.AdbPath;
                _requestedSerial = _preferences.AdbSerial;
                var devices = await RunAsync(_adb, ["devices"], cancellationToken);
                var serials = devices.Split('\n').Select(x => x.Trim().Split('\t')).Where(x => x.Length == 2 && x[1] == "device").Select(x => x[0]).ToArray();
                if (_preferences.AdbSerial.Length > 0)
                {
                    if (!serials.Contains(_preferences.AdbSerial)) return new(null, false, [], "The selected headset is not available through ADB. Keep it awake and check its USB connection.");
                    _serial = _preferences.AdbSerial;
                }
                else
                {
                    if (serials.Length != 1) return new(null, false, [], serials.Length == 0 ? "No authorized ADB device. Connect the Frame and enable Developer Mode." : "Several ADB devices are connected. Enter the Frame's serial under Connection setup.");
                    _serial = serials[0];
                }
                var port = await RunAsync(_adb, ["-s", _serial, "forward", "tcp:0", "tcp:8080"], cancellationToken);
                if (!int.TryParse(port.Trim(), out _port) || _port < 1 || _port > 65535) throw new IOException("ADB did not create a local debugger connection.");
            }
            var list = await _http.GetStringAsync($"http://127.0.0.1:{_port}/json/list", cancellationToken);
            using var targets = JsonDocument.Parse(list);
            var target = targets.RootElement.EnumerateArray().FirstOrDefault(t => t.TryGetProperty("title", out var title) && title.GetString() == "SharedJSContext");
            if (target.ValueKind == JsonValueKind.Undefined) throw new IOException("The Frame's Steam debugger is not available. Enable Developer Mode and keep Steam running on the headset.");
            var remote = new Uri(target.GetProperty("webSocketDebuggerUrl").GetString()!);
            var uri = new UriBuilder(remote) { Host = "127.0.0.1", Port = _port, Scheme = "ws" }.Uri;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(uri, timeout.Token);
            var request = JsonSerializer.Serialize(new { id = 1, method = "Runtime.evaluate", @params = new { expression = "({wifi:SystemNetworkStore.isWifiEnabled,links:streamingStore.vrLinkStatus})", returnByValue = true } });
            await socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, timeout.Token);
            while (true)
            {
                using var message = new MemoryStream();
                var buffer = new byte[8192];
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    if (received.MessageType == WebSocketMessageType.Close) throw new IOException("The headset debugger disconnected.");
                    message.Write(buffer, 0, received.Count);
                    if (message.Length > 1_048_576) throw new IOException("Headset status was too large.");
                } while (!received.EndOfMessage);
                using var doc = JsonDocument.Parse(message.ToArray());
                if (!doc.RootElement.TryGetProperty("id", out var id) || id.GetInt32() != 1) continue;
                var response = doc.RootElement.GetProperty("result");
                if (response.TryGetProperty("exceptionDetails", out _)) throw new IOException("The headset's streaming status API changed; active-link status is unavailable.");
                var value = response.GetProperty("result").GetProperty("value");
                var wifi = value.TryGetProperty("wifi", out var w) && w.ValueKind is JsonValueKind.True or JsonValueKind.False ? (bool?)w.GetBoolean() : null;
                var info = value.GetProperty("links");
                var links = new List<FrameLink>();
                bool hasInfo = info.TryGetProperty("bHasInfo", out var has) && has.GetBoolean();
                if (info.TryGetProperty("links", out var array) && array.ValueKind == JsonValueKind.Array)
                    foreach (var link in array.EnumerateArray())
                        links.Add(new(link.GetProperty("type").GetInt32(), link.GetProperty("bActive").GetBoolean(), link.GetProperty("p100").GetDouble(), link.GetProperty("loss_ratio").GetDouble()));
                return new(wifi, hasInfo, links, hasInfo ? "Active links reported by the headset" : "The headset is available; no stream is active.");
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or WebSocketException or JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            await RemoveForwardAsync();
            return new(null, false, [], ex is OperationCanceledException ? "Headset status timed out. Check Developer Mode and the USB connection." : ex.Message);
        }
    }

    private async Task<ConnectionSnapshot> ReadWirelessAsync(CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(_preferences.SshHost, @"^[a-zA-Z0-9][a-zA-Z0-9.:-]*$"))
            return new(null, false, [], "Enter the paired Frame's wireless hostname or IP under Connection setup.");
        if (!File.Exists(_preferences.SshKey) || !File.Exists(_preferences.SshKnownHosts))
            return new(null, false, [], "Wireless monitoring needs the paired Devkit key and trusted known-hosts file under Connection setup.");
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        if (!File.Exists(ssh)) return new(null, false, [], "Windows OpenSSH client is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        ProcessStartInfo info;
        try { info = FrameReceiverDiagnostics.CreateSshStartInfo(_preferences); }
        catch (ArgumentException) { return new(null, false, [], "Wireless monitoring needs a valid paired host and absolute paths to its key and trusted host file."); }
        try
        {
            using var process = Process.Start(info) ?? throw new IOException();
            try
            {
                var output = ReadBoundedAsync(process.StandardOutput, 32768, timeout.Token);
                var error = ReadBoundedAsync(process.StandardError, 8192, timeout.Token);
                await process.StandardInput.WriteAsync(FrameLinkQuery.Script.AsMemory(), timeout.Token);
                process.StandardInput.Close();
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error);
                if (process.ExitCode != 0) throw new IOException();
                using var doc = JsonDocument.Parse(await output);
                var value = doc.RootElement;
                var wifi = value.TryGetProperty("wifi", out var w) && w.ValueKind is JsonValueKind.True or JsonValueKind.False ? (bool?)w.GetBoolean() : null;
                var status = value.GetProperty("links");
                var hasInfo = status.TryGetProperty("bHasInfo", out var has) && has.ValueKind == JsonValueKind.True;
                var links = new List<FrameLink>();
                if (status.TryGetProperty("links", out var array) && array.ValueKind == JsonValueKind.Array)
                    foreach (var link in array.EnumerateArray().Take(16))
                    {
                        var type = link.GetProperty("type").GetInt32();
                        var active = link.GetProperty("bActive").GetBoolean();
                        var latency = link.GetProperty("p100").GetDouble();
                        var loss = link.GetProperty("loss_ratio").GetDouble();
                        if (!double.IsFinite(latency) || latency < 0 || !double.IsFinite(loss) || loss < 0 || loss > 1) throw new JsonException();
                        links.Add(new(type, active, latency, loss));
                    }
                return new(wifi, hasInfo, links, hasInfo ? "Active links reported by the headset over paired wireless SSH" : "The headset is available over wireless; no stream is active.");
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException or
            OperationCanceledException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new(null, false, [], "Wireless headset status unavailable. Check the paired host, trusted key and Developer Mode; Wi-Fi must remain enabled.");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + count > maximum) throw new IOException("Headset metadata exceeds the inspection limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    public async Task<string> RecoverUsbAsync(CancellationToken cancellationToken = default)
    {
        // Same pinned SSH configuration as wireless monitoring: system OpenSSH (never a PATH lookup),
        // the configured key and trusted known-hosts file, strict host checking.
        ProcessStartInfo info;
        try { info = FrameReceiverDiagnostics.CreateSshStartInfo(_preferences, "sh", "-s"); }
        catch (ArgumentException) { throw new ArgumentException("USB recovery needs the paired Frame host, an absolute path to its Devkit key, and the trusted known-hosts file under Wireless monitoring."); }
        // Wireless SSH keeps this control channel alive while the USB gadget restarts.
        // It never enables root, changes Wi-Fi, or modifies firmware.
        const string script = """
            set -eu
            python3 - <<'PY'
            import os, pathlib, hashlib, stat, json, subprocess, signal, errno
            def require(condition, message):
                if not condition: raise SystemExit(message)
            def output(*args):
                return subprocess.check_output(args, text=True, timeout=4).strip()
            def trusted(path, sha=None, text=None):
                p=pathlib.Path(path); s=p.lstat()
                require(stat.S_ISREG(s.st_mode) and s.st_uid==0 and not s.st_mode & 0o022, 'USB fix files must be root-owned and unmodified.')
                b=p.read_bytes()
                require((sha is None or hashlib.sha256(b).hexdigest()==sha) and (text is None or b.decode()==text), 'The installed USB daemon fix or firmware has changed.')
            require(os.getuid()==1000, 'Recovery requires the paired steamos user, not root.')
            require(pathlib.Path('/etc/systemd/system/adbd.service.d/steamos-devkit-enabled').exists(), 'Enable Developer Mode before recovery.')
            require(not any(pathlib.Path(p).exists() for p in ['/tmp/adbd_block','/tmp/usb_gadget_block','/run/adbd/rooted']), 'USB recovery is unavailable while root mode or USB blocking is enabled.')
            addresses=json.loads(output('ip','-j','address'))
            target=os.environ.get('SSH_CONNECTION','').split()
            wireless=[a['local'] for n in addresses if n['ifname'].startswith('wlan') and 'UP' in n.get('flags',[]) for a in n.get('addr_info',[]) if a.get('family')=='inet']
            require(len(target)==4 and target[2] in wireless, 'Use a wireless SSH address; keep Wi-Fi enabled during recovery.')
            trusted('/usr/lib/android-sdk/platform-tools/adbd',sha='242230654f59e67b5d1fb4c8a5cfb1065a37a161cc66c810a03b5f2475d84d2d')
            trusted('/var/lib/steam-frame-usb-fix/adbd-fixed',sha='07a8aad7d3a6e4858f0a8f1258dba56b2d7ac386b93d1b43330a4552fb376706')
            trusted('/var/lib/steam-frame-usb-fix/start-adbd.sh',sha='15098cfd25fe8168afd179fb59264a9b80a0cf61056d2945c2c7d6546b07fae3')
            trusted('/etc/systemd/system/adbd.service.d/90-usb-thread-join-fix.conf',text='[Unit]\nRequiresMountsFor=/var/lib/steam-frame-usb-fix\n\n[Service]\nExecStart=\nExecStart=/var/lib/steam-frame-usb-fix/start-adbd.sh\n')
            require(output('systemctl','show','adbd.service','-p','Restart','--value')=='always', 'USB daemon is not supervised for restart.')
            pid=int(output('systemctl','show','adbd.service','-p','MainPID','--value'))
            require(pid>1, 'USB daemon is stopped.')
            fd=os.pidfd_open(pid)
            try:
                p=pathlib.Path('/proc')/str(pid)
                uid=next(l for l in (p/'status').read_text().splitlines() if l.startswith('Uid:')).split()[1:]
                require(all(int(v)==1000 for v in uid) and (p/'cgroup').read_text().splitlines()==['0::/system.slice/adbd.service'], 'USB daemon ownership or service identity changed.')
                require((p/'comm').read_text().strip()=='adbd-fixed', 'USB daemon executable identity changed.')
                try: require(os.readlink(p/'exe')=='/var/lib/steam-frame-usb-fix/adbd-fixed', 'USB daemon executable changed.')
                except OSError as e: require(e.errno==errno.EACCES, 'Cannot verify USB daemon executable.')
                require(int(output('systemctl','show','adbd.service','-p','MainPID','--value'))==pid, 'USB daemon changed; retry recovery.')
                signal.pidfd_send_signal(fd,signal.SIGTERM)
                print('USB daemon restart requested. Keep Wi-Fi on and wait for the USB adapter to return.')
            finally: os.close(fd)
            PY
            """;
        return await RunAsync(info, cancellationToken, script.Replace("\r\n", "\n") + "\n");
    }

    public async Task ResetDisplayPreferenceAsync(CancellationToken cancellationToken = default)
    {
        var info = FrameReceiverDiagnostics.CreateSshStartInfo(_preferences);
        const string script = """
            import ctypes as c,json
            lib=c.CDLL('/opt/steamvr/bin/linuxarm64/libopenvr_api.so')
            lib.VR_InitInternal2.argtypes=[c.POINTER(c.c_int),c.c_int,c.c_char_p];lib.VR_InitInternal2.restype=c.c_uint32
            lib.VR_GetGenericInterface.argtypes=[c.c_char_p,c.POINTER(c.c_int)];lib.VR_GetGenericInterface.restype=c.c_void_p
            error=c.c_int();lib.VR_InitInternal2(c.byref(error),3,None)
            if error.value:raise RuntimeError('Frame OpenVR is unavailable')
            try:
             p=lib.VR_GetGenericInterface(b'FnTable:IVRSettings_003',c.byref(error))
             if error.value or not p:raise RuntimeError('Frame settings are unavailable')
             table=c.cast(p,c.POINTER(c.c_void_p))
             remove=c.CFUNCTYPE(None,c.c_char_p,c.c_char_p,c.POINTER(c.c_int))(table[10])
             remove(b'steamvr',b'preferredRefreshRate',c.byref(error))
             if error.value not in (0,5):raise RuntimeError('Frame display preference could not be reset')
             print(json.dumps({'display_preference_reset':True}))
            finally:lib.VR_ShutdownInternal()
            """;
        var result = await RunAsync(info, cancellationToken, script.Replace("\r\n", "\n") + "\n");
        using var json = JsonDocument.Parse(result);
        if (!json.RootElement.GetProperty("display_preference_reset").GetBoolean()) throw new IOException("Frame reset was not confirmed.");
    }

    private async Task RemoveForwardAsync()
    {
        if (_port != 0)
        {
            try { await RunAsync(_adb, ["-s", _serial, "forward", "--remove", $"tcp:{_port}"], CancellationToken.None); }
            catch (Exception) { /* Cable may already be gone. No other device/server is touched. */ }
            _port = 0;
        }
    }
    private static Task<string> RunAsync(string file, IEnumerable<string> args, CancellationToken cancellationToken, string? input = null)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return RunAsync(info, cancellationToken, input);
    }
    private static async Task<string> RunAsync(ProcessStartInfo info, CancellationToken cancellationToken, string? input = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        info.RedirectStandardInput = input != null;
        using var process = Process.Start(info) ?? throw new IOException($"Could not start {Path.GetFileName(info.FileName)}.");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            if (input != null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
            await process.WaitForExitAsync(timeout.Token);
            var text = await output; var errors = await error;
            if (process.ExitCode != 0) throw new IOException(errors.Length > 0 ? errors.Trim() : text.Trim());
            return text;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
    public async ValueTask DisposeAsync() { await RemoveForwardAsync(); _http.Dispose(); }
}
