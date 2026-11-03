using System.Diagnostics;
using System.Text.Json;

namespace CodecSwapper;

/// <summary>
/// The moving test scene (SteamFrameBenchmark.exe): a world-fixed room with a changing high-detail texture that the headset shows while the benchmark runs,
/// so no game is needed. It is submitted through SteamVR's compositor, so it takes the headset view over from any running VR app. The scene stops itself
/// if the heartbeat from this process stops, so it cannot outlive the tray.
/// </summary>
public sealed class BenchmarkScene : IAsyncDisposable
{
    private Process? _process;
    private readonly CancellationTokenSource _stop = new();
    private Task? _reader, _heartbeat, _errors;
    private long _frames = -1, _submitErrors;
    private bool _disposed;

    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "Native", "SteamFrameBenchmark.exe");
    public static bool Available => File.Exists(ExecutablePath);

    public long Frames => Interlocked.Read(ref _frames);
    public long SubmitErrors => Interlocked.Read(ref _submitErrors);
    public bool Running => _process is { HasExited: false };

    /// <summary>Starts the scene and waits until it submits frames. Throws when SteamVR cannot take it.</summary>
    public async Task StartAsync(string openVrApiPath, CancellationToken token)
    {
        if (!Available) throw new IOException("The test scene (Native\\SteamFrameBenchmark.exe) is missing from this build. Untick the test scene or use a complete package.");
        var info = new ProcessStartInfo(ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(openVrApiPath);
        info.ArgumentList.Add("--unattended");
        var process = _process = Process.Start(info) ?? throw new IOException("The test scene could not be started.");
        _reader = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync(_stop.Token) is { } line)
                {
                    try
                    {
                        using var json = JsonDocument.Parse(line);
                        Interlocked.Exchange(ref _frames, json.RootElement.GetProperty("Frames").GetInt64());
                        Interlocked.Exchange(ref _submitErrors, json.RootElement.GetProperty("SubmitErrors").GetInt64());
                    }
                    catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        });
        _errors = Task.Run(async () =>
        {
            try { var buffer = new char[512]; while (await process.StandardError.ReadAsync(buffer, _stop.Token) != 0) { } }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        });
        _heartbeat = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await process.StandardInput.WriteLineAsync("heartbeat");
                    await process.StandardInput.FlushAsync(_stop.Token);
                    await Task.Delay(1000, _stop.Token);
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        });
        for (var i = 0; i < 80; i++)
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new IOException("SteamVR did not accept the test scene (exit code " + process.ExitCode + "). Is SteamVR running with the headset connected?");
            if (Frames > 0) return;
            await Task.Delay(250, token);
        }
        throw new IOException("The test scene started but submitted no frames. Is the headset awake?");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (_process is not null)
        {
            try { _process.StandardInput.Close(); } catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { try { if (!_process.HasExited) _process.Kill(); } catch (InvalidOperationException) { } }
            foreach (var task in new[] { _reader, _heartbeat, _errors }.OfType<Task>())
                try { await task; } catch (Exception) { /* closing the pipes ends the readers */ }
            _process.Dispose();
        }
        _stop.Dispose();
    }
}
