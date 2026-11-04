using System.Diagnostics;
using SteamFrame.Core;

namespace CodecSwapper;

public sealed partial class TrayApp
{
    private BenchmarkForm? _benchmarkForm;
    private CancellationTokenSource? _benchmarkCancel;
    private Task? _benchmarkTask;

    private async void ShowBenchmark()
    {
        // The stream stats monitor opens with the benchmark, so the numbers can be watched while it runs.
        if (!_stats.Visible) _stats.Show();
        if (_benchmarkForm is { IsDisposed: false } open) { open.Show(); open.Activate(); return; }
        var patched = false;
        try { patched = (await _patch.InspectAsync()).IsPatched; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        var form = _benchmarkForm = new BenchmarkForm(patched);
        form.StartRequested += settings => _benchmarkTask = RunAsync(() => BenchmarkAsync(form, settings));
        form.SweepRequested += (settings, useScene) => _benchmarkTask = RunAsync(() => EncoderSweepAsync(form, settings, useScene));
        form.ApplyRequested += choice => _ = RunAsync(async () =>
        {
            try { await ApplyEncoderChoiceAsync(choice, CancellationToken.None); Balloon("Encoder settings", "Applied: " + choice.Describe(), ToolTipIcon.Info); form.ShowApplyResult(choice, true, choice.Describe()); }
            catch (Exception error) when (error is IOException or InvalidOperationException) { Balloon("Encoder settings", "Not applied: " + error.Message, ToolTipIcon.Warning); form.ShowApplyResult(choice, false, error.Message); }
        });
        form.StopRequested += () => _benchmarkCancel?.Cancel();
        form.DeleteRequested += run =>
        {
            try
            {
                if (run is BenchmarkRun benchmark) BenchmarkReport.Delete(AppData.BenchmarksDirectory, benchmark);
                else if (run is EncoderSweepRun sweep) EncoderSweepReport.Delete(AppData.BenchmarksDirectory, sweep);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Balloon("Benchmark", "The run could not be deleted: " + error.Message, ToolTipIcon.Warning); }
        };
        form.SetHistory(BenchmarkReport.LoadAll(AppData.BenchmarksDirectory));
        form.SetSweepHistory(EncoderSweepReport.LoadAll(AppData.BenchmarksDirectory));
        form.SetProbe(_controller.TargetMbps);
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

    /// <summary>Whether this benchmark started SteamVR itself (then it also closes it when the run ends).</summary>
    private bool _steamVrLaunchedByBenchmark;

    /// <summary>
    /// After a run the headset's stream and SteamVR are closed, so the next run starts from a clean session and not from whatever state the last one left:
    /// when the benchmark started SteamVR, or showed its own test scene (so nobody was playing). With the user's own VR app left running it stays open.
    /// </summary>
    private async Task CloseSteamVrAfterRunAsync(BenchmarkForm form, bool useScene)
    {
        if (!_steamVrLaunchedByBenchmark && !useScene) return;
        _steamVrLaunchedByBenchmark = false;
        // The headset first: while the Frame still holds the stream it starts SteamVR again by itself a moment after the PC side is closed.
        form.ShowStatus("Disconnecting the headset...");
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            if (await new FrameStreamControl(_preferences).StopAsync(limit.Token)) { TrayLog("Benchmark stopped the stream on the Frame"); await Task.Delay(FrameStreamControl.SettleTime); }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ArgumentException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            TrayLog("Benchmark could not stop the stream on the Frame: " + error.Message);
        }
        if (!OpenVrSettings.IsRunning) return;
        form.ShowStatus("Closing SteamVR so the next run starts clean...");
        TrayLog("Benchmark closes SteamVR");
        try
        {
            if (!await SteamVrProcess.StopAsync(_paths.SteamVrRoot, TimeSpan.FromSeconds(30), CancellationToken.None))
                Balloon("Benchmark", "SteamVR did not close; close it before the next run.", ToolTipIcon.Warning);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { TrayLog("Closing SteamVR failed: " + error.Message); }
    }

    /// <summary>Starts SteamVR when it is not running (the headset on the desk then connects by itself) and waits until it is up.</summary>
    private async Task EnsureSteamVrAsync(BenchmarkForm form, CancellationToken token)
    {
        // A benchmark of a stale hook measures the wrong thing, so the SteamVR driver has to be this tray's version before the run.
        var outdated = false;
        try { outdated = _driver.Inspect() == SteamVrDriverState.Outdated; } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (outdated && OpenVrSettings.IsRunning)
            throw new InvalidOperationException("The SteamVR driver installed for this tray is an older version than this tray, so the benchmark would measure the old hook. Close SteamVR and start the benchmark again; it installs the new driver and starts SteamVR itself.");
        if (outdated)
        {
            form.ShowStatus("Updating the SteamVR driver...");
            TrayLog("Benchmark updates the SteamVR driver before starting SteamVR");
            try { _driver.Install(); } catch (UnauthorizedAccessException error) { throw new IOException("The SteamVR driver could not be updated: " + error.Message); }
        }
        _steamVrLaunchedByBenchmark = false;
        if (OpenVrSettings.IsRunning) return;
        var startup = Path.Combine(_paths.SteamVrRoot, "bin", "win64", "vrstartup.exe");
        form.ShowStatus("Starting SteamVR...");
        TrayLog("Benchmark starts SteamVR");
        try
        {
            System.Diagnostics.Process.Start(File.Exists(startup)
                ? new System.Diagnostics.ProcessStartInfo(startup) { UseShellExecute = false, CreateNoWindow = true }
                : new System.Diagnostics.ProcessStartInfo("steam://rungameid/250820") { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { throw new IOException("SteamVR could not be started: " + error.Message); }
        _steamVrLaunchedByBenchmark = true;
        for (var seconds = 0; seconds < 90; seconds++)
        {
            if (OpenVrSettings.IsRunning) { await Task.Delay(TimeSpan.FromSeconds(3), token); return; }
            await Task.Delay(1000, token);
        }
        throw new IOException("SteamVR did not start within 90 seconds.");
    }

    /// <summary>
    /// The scene can only attach once SteamVR has a headset, so it is retried until the Frame has connected (up to two minutes).
    /// </summary>
    private async Task<BenchmarkScene> StartSceneWhenHeadsetConnectsAsync(BenchmarkForm form, CancellationToken token)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var scene = new BenchmarkScene();
            form.ShowStatus(waited.Elapsed.TotalSeconds < 2 ? "Starting the test scene on the headset..." : $"Waiting for the headset to connect... {waited.Elapsed.TotalSeconds:0} s");
            try { await scene.StartAsync(_paths.OpenVrApi, token); return scene; }
            catch (IOException error)
            {
                await scene.DisposeAsync();
                if (waited.Elapsed > TimeSpan.FromSeconds(120))
                    throw new IOException("The headset did not connect within two minutes, so the test scene could not start (" + error.Message + ") Check that the Frame is on, awake and paired to this PC.");
            }
            catch { await scene.DisposeAsync(); throw; }
            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
    }

    /// <summary>What a run needs around it: the headset kept awake, SteamVR running and the test scene shown. Disposing releases them.</summary>
    private sealed class BenchmarkEnvironment(BenchmarkScene? scene, FrameActivity.Keeper? awake) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (scene is not null) await scene.DisposeAsync();
            if (awake is not null) await awake.DisposeAsync();
        }
    }

    private async Task<BenchmarkEnvironment> StartEnvironmentAsync(BenchmarkForm form, bool useScene, List<string> setup, CancellationToken token)
    {
        FrameActivity.Keeper? awake = null;
        BenchmarkScene? scene = null;
        try
        {
            try
            {
                awake = new FrameActivity.Keeper(_preferences);
                form.ShowStatus("Keeping the headset awake...");
                await awake.StartAsync(token);
                setup.Add("headset kept awake by the tray");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                TrayLog("Benchmark could not extend the headset activity: " + error.Message);
                await awake!.DisposeAsync(); awake = null;
                form.ShowStatus("The Frame could not be asked to stay awake; keep the headset awake yourself.");
            }
            await EnsureSteamVrAsync(form, token);
            if (useScene)
            {
                setup.Add("test scene: built-in moving room");
                scene = await StartSceneWhenHeadsetConnectsAsync(form, token);
                TrayLog("Benchmark test scene running");
            }
            else setup.Add("test scene: own VR app");
            return new BenchmarkEnvironment(scene, awake);
        }
        catch
        {
            if (scene is not null) await scene.DisposeAsync();
            if (awake is not null) await awake.DisposeAsync();
            await CloseSteamVrAfterRunAsync(form, useScene);
            throw;
        }
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
        BenchmarkEnvironment? environment = null;
        try
        {
            environment = await StartEnvironmentAsync(form, settings.UseScene, run.Setup, cancel.Token);
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
            if (environment is not null) await environment.DisposeAsync();
            _statsService.BenchmarkActive = false;
            _benchmarkCancel = null;
            try { await _controller.ChangeTargetAsync(original, () => Task.FromResult<string?>(null), _ => Task.FromResult(false)); }
            catch (Exception error) when (error is not OperationCanceledException) { Balloon("Benchmark", $"Your bitrate target could not be put back to {original} Mbps: {error.Message}", ToolTipIcon.Warning); }
            await CloseSteamVrAfterRunAsync(form, settings.UseScene);
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

    /// <summary>Puts encoder options into force on the running stream, or says why that cannot be done live.</summary>
    private async Task ApplyEncoderChoiceAsync(EncoderChoice choice, CancellationToken token)
    {
        _preferences.HookRateControl = choice.RateControl;
        _preferences.HookPreset = choice.Preset;
        _preferences.HookVbvFrames = choice.VbvFrames;
        _preferences.HookMultiPass = choice.MultiPass;
        await SaveEncoderOptionsAsync();
        if (!await _controller.ApplyEncoderOptionsAsync(token))
            throw new InvalidOperationException("The encoder options cannot be applied to the running stream (is SteamVR streaming?).");
    }

    private async Task EncoderSweepAsync(BenchmarkForm form, EncoderSweepSettings settings, bool useScene)
    {
        var original = _controller.TargetMbps;
        var baseline = EncoderChoice.From(_preferences);
        // Testing the current settings changes nothing: it runs at the bitrate target in force and puts nothing back afterwards.
        var observeOnly = settings.Goal == EncoderGoal.Current;
        if (observeOnly) settings = settings with { ProbeMbps = original };
        var run = new EncoderSweepRun { At = DateTimeOffset.Now, Setup = BenchmarkSetup() };
        using var cancel = _benchmarkCancel = new CancellationTokenSource();
        form.SetRunning(true);
        _statsService.BenchmarkActive = true;
        TrayLog($"Encoder sweep started at {settings.ProbeMbps} Mbps, {settings.Measure.TotalSeconds:0} s per window, from {baseline.Describe()}");
        string? saved = null;
        BenchmarkEnvironment? environment = null;
        Func<EncoderChoice, CancellationToken, Task> apply = async (choice, token) => { TrayLog("Sweep applies " + choice.Describe()); await ApplyEncoderChoiceAsync(choice, token); };
        try
        {
            environment = await StartEnvironmentAsync(form, useScene, run.Setup, cancel.Token);
            var measure = new BenchmarkRunner(
                (mbps, token) => _controller.ChangeTargetAsync(mbps, () => throw new InvalidOperationException("This session cannot change the bitrate without restarting, so the sweep cannot run. Restart SteamVR and try again."), _ => Task.FromResult(false), token),
                NextBenchmarkSampleAsync);
            measure.TolerableResets = 2;
            if (!observeOnly) await measure.ApplyTargetAsync(settings.ProbeMbps, cancel.Token);
            await new EncoderSweepRunner(measure, apply).RunAsync(settings, run, baseline, new Progress<EncoderSweepProgress>(form.ShowSweepProgress), cancel.Token);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            run.Error = error.Message;
            run.Conclusion = EncoderSweepRunner.Conclude(run);
        }
        finally
        {
            if (environment is not null) await environment.DisposeAsync();
            _statsService.BenchmarkActive = false;
            _benchmarkCancel = null;
            // The user's own encoder options and bitrate come back whatever happened; the recommendation is applied only on request.
            if (!observeOnly)
            {
            try
            {
                if (_steamVrLaunchedByBenchmark || useScene)
                {
                    // SteamVR is closed right after this, so only the saved options need putting back: the next start builds its encoder from them.
                    _preferences.HookRateControl = baseline.RateControl; _preferences.HookPreset = baseline.Preset; _preferences.HookVbvFrames = baseline.VbvFrames; _preferences.HookMultiPass = baseline.MultiPass;
                    await SaveEncoderOptionsAsync();
                }
                else await apply(baseline, CancellationToken.None);
            }
            catch (Exception error) when (error is not OperationCanceledException) { Balloon("Encoder sweep", $"Your encoder options could not be put back ({baseline.Describe()}): {error.Message}", ToolTipIcon.Warning); }
            try { await _controller.ChangeTargetAsync(original, () => Task.FromResult<string?>(null), _ => Task.FromResult(false)); }
            catch (Exception error) when (error is not OperationCanceledException) { Balloon("Encoder sweep", $"Your bitrate target could not be put back to {original} Mbps: {error.Message}", ToolTipIcon.Warning); }
            }
            await CloseSteamVrAfterRunAsync(form, useScene);
        }
        if (run.Trials.Count > 0)
        {
            try { saved = EncoderSweepReport.Save(AppData.BenchmarksDirectory, run); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Balloon("Encoder sweep", "The results could not be saved: " + error.Message, ToolTipIcon.Warning); }
        }
        TrayLog("Encoder sweep ended: " + run.Conclusion);
        form.SetRunning(false);
        form.SetSweepHistory(EncoderSweepReport.LoadAll(AppData.BenchmarksDirectory).Where(r => r.At != run.At).ToList());
        form.ShowSweep(run, saved);
        if (!_exiting) Balloon("Encoder sweep", run.Conclusion, run.Error is null ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }
}
