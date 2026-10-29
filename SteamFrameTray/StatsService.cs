namespace SteamFrameTray;

/// <summary>
/// Owns the one-second sampling for the stats window and the run log. It samples only while the window is open or a run
/// is being recorded, so the encoder probe stays idle otherwise.
/// </summary>
public sealed class StatsService : IDisposable
{
    private readonly StatsSampler _sampler;
    private readonly Func<TraySnapshot> _tray;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private RunLog? _log;
    private bool _sampling, _viewing;

    public StatsService(StatsSampler sampler, Func<TraySnapshot> tray)
    {
        _sampler = sampler; _tray = tray;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public event Action<StatsSample, TraySnapshot>? Sampled;
    public StatsSampler Sampler => _sampler;
    public bool Recording => _log is not null;
    public string? LogPath => _log?.Path;

    public bool Viewing
    {
        get => _viewing;
        set { _viewing = value; Update(); }
    }

    /// <summary>Starts a new CSV in the logs folder; the header lines describe the setup being measured.</summary>
    public string StartRecording(IEnumerable<string> header)
    {
        if (_log is not null) return _log.Path;
        _log = new RunLog(TrayData.LogsDirectory, header, DateTimeOffset.Now);
        Update();
        return _log.Path;
    }

    public void StopRecording()
    {
        var log = _log;
        _log = null;
        log?.Dispose();
        Update();
    }

    private void Update()
    {
        var wanted = _viewing || _log is not null;
        if (wanted == _timer.Enabled) return;
        _timer.Enabled = wanted;
        if (wanted) _ = TickAsync();
    }

    private async Task TickAsync()
    {
        if (_sampling || !(_viewing || _log is not null)) return;
        _sampling = true;
        try
        {
            var sample = await _sampler.SampleAsync();
            var tray = _tray();
            _log?.Write(sample, tray, _sampler.RecentAverage);
            Sampled?.Invoke(sample, tray);
        }
        catch (IOException error)
        {
            // The log's disk went away; keep the window working and stop writing.
            StopRecording();
            Sampled?.Invoke(new(DateTimeOffset.Now, null, null, "—", false, "", false, 0, "Run log stopped: " + error.Message), _tray());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Sampled?.Invoke(new(DateTimeOffset.Now, null, null, "—", false, "", false, 0, "Stats unavailable: " + error.Message), _tray());
        }
        finally { _sampling = false; }
    }

    public void Dispose()
    {
        _timer.Dispose();
        StopRecording();
    }
}
