using System.Diagnostics;

namespace CodecSwapper;

/// <summary>Runs the HUD helper process for the tray and restarts it (after a pause) if it dies. Messages arrive on the thread that created this object.</summary>
public sealed class HudHostClient(Action<HudMessage> onMessage, Action<string> log) : IDisposable
{
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    private Process? _process;
    private bool _wanted;
    private int _restarts;
    private DateTime _startedAt;
    private System.Threading.Timer? _retry;

    public bool Running => _process is { HasExited: false };

    public void Start()
    {
        _wanted = true;
        if (Running) return;
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!, "--hud-host") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            var process = Process.Start(info) ?? throw new InvalidOperationException("The HUD helper did not start.");
            _process = process;
            _startedAt = DateTime.UtcNow;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _ui.Post(_ => OnExited(process), null);
            _ = Task.Run(() => Pump(process));
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { log("HUD helper could not start: " + error.Message); }
    }

    public void Stop()
    {
        _wanted = false;
        _retry?.Dispose();
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine(new HudMessage { Type = "quit" }.ToLine());
                process.StandardInput.Close();
                if (!process.WaitForExit(1500)) process.Kill();
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException) { try { process.Kill(); } catch (Exception) { /* already gone */ } }
        process.Dispose();
    }

    public void Send(HudMessage message)
    {
        var process = _process;
        if (process is null || process.HasExited) return;
        try { lock (process) { process.StandardInput.WriteLine(message.ToLine()); process.StandardInput.Flush(); } }
        catch (Exception error) when (error is IOException or InvalidOperationException) { /* the exit handler restarts it */ }
    }

    private void Pump(Process process)
    {
        try
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
                if (HudMessage.Parse(line) is { } message) _ui.Post(_ => { if (ReferenceEquals(_process, process)) onMessage(message); }, null);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException) { /* ended */ }
    }

    private void OnExited(Process process)
    {
        if (!ReferenceEquals(_process, process)) return;
        int code; try { code = process.ExitCode; } catch (InvalidOperationException) { code = -1; }
        log($"HUD helper exited (code 0x{code:X}) after {(DateTime.UtcNow - _startedAt).TotalSeconds:0} s");
        _process = null;
        process.Dispose();
        if (!_wanted) return;
        // A helper that ran for a while gets a fresh start; one that dies at once is retried a few times, then left off.
        _restarts = DateTime.UtcNow - _startedAt > TimeSpan.FromMinutes(1) ? 0 : _restarts + 1;
        if (_restarts > 5) { log("HUD helper keeps failing; turn the HUD off and on to try again."); return; }
        _retry?.Dispose();
        _retry = new System.Threading.Timer(_ => _ui.Post(_ => { if (_wanted) Start(); }, null), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => Stop();
}
