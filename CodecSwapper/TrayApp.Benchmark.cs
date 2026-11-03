using SteamFrame.Core;

namespace CodecSwapper;

public sealed partial class TrayApp
{
    private BenchmarkForm? _benchmarkForm;
    private CancellationTokenSource? _benchmarkCancel;
    private Task? _benchmarkTask;

    private async void ShowBenchmark()
    {
        if (_benchmarkForm is { IsDisposed: false } open) { open.Show(); open.Activate(); return; }
        var patched = false;
        try { patched = (await _patch.InspectAsync()).IsPatched; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        var form = _benchmarkForm = new BenchmarkForm(patched);
        form.StartRequested += settings => _benchmarkTask = RunAsync(() => BenchmarkAsync(form, settings));
        form.StopRequested += () => _benchmarkCancel?.Cancel();
        form.SetHistory(BenchmarkReport.LoadAll(AppData.BenchmarksDirectory));
        form.Show();
    }

    /// <summary>The next one-second stats sample, or an error when none arrives (stats stopped, tray closing).</summary>
    private async Task<StatsSample> NextBenchmarkSampleAsync(CancellationToken token)
    {
        var arrived = new TaskCompletionSource<StatsSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(StatsSample sample, TraySnapshot tray) => arrived.TrySetResult(sample);
        _statsService.Sampled += Handler;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { return await arrived.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidOperationException("The stream stats stopped arriving."); }
        }
        finally { _statsService.Sampled -= Handler; }
    }

    private List<string> BenchmarkSetup()
    {
        var lines = new List<string>
        {
            $"rate control {_preferences.HookRateControl.ToUpperInvariant()}", $"preset P{_preferences.HookPreset}", $"VBV {_preferences.HookVbvFrames} frame(s)", $"multipass {_preferences.HookMultiPass}"
        };
        try { if (OpenVrSettings.IsRunning) lines.Add($"stream width {OpenVrSettings.Read(_paths).StreamWidth}"); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { }
        return lines;
    }

    private async Task BenchmarkAsync(BenchmarkForm form, BenchmarkSettings settings)
    {
        var original = _controller.TargetMbps;
        var run = new BenchmarkRun { At = DateTimeOffset.Now, Setup = BenchmarkSetup() };
        using var cancel = _benchmarkCancel = new CancellationTokenSource();
        form.SetRunning(true);
        _statsService.BenchmarkActive = true;
        TrayLog($"Benchmark started: {settings.StartMbps}-{settings.CeilingMbps} Mbps in steps of {settings.StepMbps}, {settings.Measure.TotalSeconds:0} s each");
        string? saved = null;
        BenchmarkScene? scene = null;
        FrameActivity.Keeper? awake = null;
        try
        {
            try
            {
                awake = new FrameActivity.Keeper(_preferences);
                form.ShowStatus("Keeping the headset awake...");
                await awake.StartAsync(cancel.Token);
                run.Setup.Add("headset kept awake by the tray");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                TrayLog("Benchmark could not extend the headset activity: " + error.Message);
                await awake!.DisposeAsync(); awake = null;
                form.ShowStatus("The Frame could not be asked to stay awake; keep the headset awake yourself.");
            }
            if (settings.UseScene)
            {
                run.Setup.Add("test scene: built-in moving room");
                scene = new BenchmarkScene();
                form.ShowStatus("Starting the test scene on the headset...");
                await scene.StartAsync(_paths.OpenVrApi, cancel.Token);
                TrayLog("Benchmark test scene running");
            }
            else run.Setup.Add("test scene: own VR app");
            var runner = new BenchmarkRunner(
                // A session that cannot change the rate live would have to restart the stream; the benchmark never does that.
                (mbps, token) => _controller.ChangeTargetAsync(mbps, () => throw new InvalidOperationException("This session cannot change the bitrate without restarting, so the benchmark cannot run. Restart SteamVR and try again."), _ => Task.FromResult(false), token),
                NextBenchmarkSampleAsync);
            await runner.RunAsync(settings, run, new Progress<BenchmarkProgress>(form.ShowProgress), cancel.Token);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            run.Error = error.Message;
            if (run.Steps.Count == 0) run.Conclusion = error.Message;
        }
        finally
        {
            if (scene is not null) await scene.DisposeAsync();
            if (awake is not null) await awake.DisposeAsync();
            _statsService.BenchmarkActive = false;
            _benchmarkCancel = null;
            try { await _controller.ChangeTargetAsync(original, () => Task.FromResult<string?>(null), _ => Task.FromResult(false)); }
            catch (Exception error) when (error is not OperationCanceledException) { Balloon("Benchmark", $"Your bitrate target could not be put back to {original} Mbps: {error.Message}", ToolTipIcon.Warning); }
        }
        if (run.Steps.Count > 0)
        {
            try { saved = BenchmarkReport.Save(AppData.BenchmarksDirectory, run); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Balloon("Benchmark", "The results could not be saved: " + error.Message, ToolTipIcon.Warning); }
        }
        TrayLog("Benchmark ended: " + run.Conclusion);
        form.SetRunning(false);
        form.SetHistory(BenchmarkReport.LoadAll(AppData.BenchmarksDirectory).Where(r => r.At != run.At).ToList());
        form.ShowRun(run, saved);
        if (!_exiting) Balloon("Bitrate benchmark", run.Conclusion, run.Error is null ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }
}
