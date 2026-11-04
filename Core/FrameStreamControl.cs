using System.Text;
using System.Text.Json;

namespace SteamFrame.Core;

/// <summary>
/// Frame-side stream control for changes that resize the picture. The Frame's Steam Link decoder resizes in place when the stream size changes, and that
/// sometimes glitches; while another program is encoding on the Frame's video chip (a screen recorder) the decoder's own recovery then fails and the
/// stream is lost. Restarting the stream around the change gives the decoder a fresh start at the new size instead.
/// </summary>
public sealed class FrameStreamControl
{
    /// <summary>Runs a Python script on the Frame (over the paired SSH session) and returns what it printed.</summary>
    private readonly Func<string, CancellationToken, Task<string>> _run;
    private readonly string _runtimeDirectory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public FrameStreamControl(FramePreferences preferences, string? runtimeDirectory = null)
        : this((script, token) => FrameSsh.ExecuteAsync(preferences, script, token), runtimeDirectory) { }

    public FrameStreamControl(Func<string, CancellationToken, Task<string>> run, string? runtimeDirectory = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _run = run;
        _runtimeDirectory = runtimeDirectory ?? Path.Combine(AppContext.BaseDirectory, "CodecLab");
        _delay = delay ?? ((time, token) => Task.Delay(time, token));
    }

    /// <summary>How long the decoder is given to let go after the stream stops, before the picture size changes.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(1500);

    // Programs holding one of the Frame's hardware video encoder nodes (the Steam Link client only uses the decoder).
    internal const string EncoderUsersScript = """
        import glob,json,os
        encoders={'/dev/'+os.path.basename(d) for d in glob.glob('/sys/class/video4linux/video*') if 'encoder' in (open(d+'/name').read() if os.path.exists(d+'/name') else '')}
        users=set()
        for process in glob.glob('/proc/[0-9]*'):
            try:
                name=open(process+'/comm').read().strip()
                for entry in os.listdir(process+'/fd'):
                    try:
                        if os.readlink(process+'/fd/'+entry) in encoders:users.add(name)
                    except OSError:pass
            except OSError:pass
        print(json.dumps(sorted(users)))
        """;

    /// <summary>The names of the programs that currently have the Frame's video encoder open; empty when none does.</summary>
    public async Task<IReadOnlyList<string>> EncoderUsersAsync(CancellationToken token = default)
    {
        var reply = await _run(EncoderUsersScript, token).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<string[]>(reply.Trim()) ?? []; }
        catch (JsonException error) { throw new IOException("The Frame's reply about its video encoder could not be read.", error); }
    }

    /// <summary>Evaluates a read-only JavaScript expression in the Frame's Steam UI and returns the JSON text of its value (a string value comes back quoted).</summary>
    public async Task<string> EvaluateAsync(string expression, CancellationToken token = default)
    {
        var template = await File.ReadAllTextAsync(Path.Combine(_runtimeDirectory, "frame_cef_stdio.py"), token).ConfigureAwait(false);
        var script = template.Replace("__EXPRESSION_BASE64__", Convert.ToBase64String(Encoding.UTF8.GetBytes(expression)));
        return (await _run(script, token).ConfigureAwait(false)).Trim();
    }

    /// <summary>Stops the VR stream on the Frame; false when there was none to stop.</summary>
    public async Task<bool> StopAsync(CancellationToken token = default) => (await CefAsync("stop", token).ConfigureAwait(false)).Contains("\"requested\"", StringComparison.Ordinal);

    public Task StartAsync(CancellationToken token = default) => CefAsync("start", token);

    private async Task<string> CefAsync(string which, CancellationToken token)
    {
        var expression = await File.ReadAllTextAsync(Path.Combine(_runtimeDirectory, which + "_stream.js"), token).ConfigureAwait(false);
        var template = await File.ReadAllTextAsync(Path.Combine(_runtimeDirectory, "frame_cef_stdio.py"), token).ConfigureAwait(false);
        var script = template.Replace("__EXPRESSION_BASE64__", Convert.ToBase64String(Encoding.UTF8.GetBytes(expression)));
        var reply = await _run(script, token).ConfigureAwait(false);
        if (reply.Contains("\"error\"", StringComparison.Ordinal)) throw new IOException("The Frame refused the stream " + which + ".");
        return reply;
    }

    /// <summary>
    /// Runs a change that resizes the stream. When another program has the Frame's encoder open the stream is stopped first and started again after the
    /// change; the programs' names are returned. When none has, nothing is touched and null is returned, so the caller applies the change live.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ChangeWithRestartAsync(Func<Task> change, CancellationToken token = default)
    {
        IReadOnlyList<string> users;
        try { users = await EncoderUsersAsync(token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ArgumentException or FileNotFoundException or UnauthorizedAccessException) { return null; }   // the Frame cannot be asked: change live as before
        if (users.Count == 0) return null;
        var stopped = await StopAsync(token).ConfigureAwait(false);
        try
        {
            if (stopped) await _delay(SettleTime, token).ConfigureAwait(false);
            await change().ConfigureAwait(false);
        }
        finally
        {
            // Whatever happened to the change, the stream that was stopped comes back.
            if (stopped) await StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return users;
    }
}
