using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SteamLinkVRSettings.Frame;
using Windows.UI;

namespace SteamLinkVRSettings.Views;

/// <summary>
/// Wireless link readouts: SteamVR's own link estimate, held frames and capacity histogram from the
/// driver log, the dongle's radio association from Windows, the advisor and the quick capacity sweep.
/// Auto tune remains the full comparison; the sweep only walks the manual target during a real stream.
/// </summary>
public sealed partial class FramePage
{
    private static readonly Color Amber = Color.FromArgb(255, 255, 185, 83), Neutral = Color.FromArgb(255, 200, 200, 208), Good = Color.FromArgb(255, 92, 189, 118), Bad = Color.FromArgb(255, 240, 120, 110);
    private static readonly TimeSpan FreshLimit = TimeSpan.FromSeconds(WirelessAdvisor.FreshSeconds);
    private Task<WirelessLinkSnapshot>? _radioRead;
    private WirelessLinkSnapshot _radio = WirelessLinkSnapshot.Unknown("checking…");
    private double _nextRadioRead;
    private bool? _liveAutomatic;
    private DateTimeOffset _targetChangedAt = DateTimeOffset.MinValue;
    private double? _previousTarget;
    private WirelessAdvice _advice = WirelessAdvice.None;
    private SweepRun? _sweep;
    private Task? _sweepTask;
    private CancellationTokenSource? _sweepCancellation;
    private SweepConclusion? _sweepConclusion;
    private DateTimeOffset _sweepConcludedAt;
    private bool _sweepRunning;

    private sealed class SweepRun
    {
        public required IReadOnlyList<int> Plan;
        public int Index;
        public readonly List<SweepStepResult> Results = [];
        public double OriginalTarget;
        public bool OriginalAutomatic;
        public bool Measuring;
        public DateTimeOffset MeasureStarted;
        /// <summary>When the step's target was applied: a SteamVR step-down anywhere after this counts against the step.</summary>
        public DateTimeOffset StepStarted;
        public long BaseThrottles, BaseBadLinks, BaseChanges, BaseResets, BaseRetries, BaseDrops;
        public double BaseThrottleMs;
        public double? LowestDriver, MaxLoss, MaxLatency;
        public readonly List<double> Video = [], Fps = [];
        public int CurrentTarget => Plan[Index];
    }

    private void PollRadio(double at)
    {
        if (_radioRead is { IsCompleted: true })
        {
            _radio = _radioRead.IsCompletedSuccessfully ? _radioRead.Result : WirelessLinkSnapshot.Unknown("Wireless status read failed.");
            _ = _radioRead.Exception;
            _radioRead = null;
        }
        if (_radioRead is null && at >= _nextRadioRead)
        {
            _radioRead = Task.Run(WirelessLinkStatus.Read);
            _nextRadioRead = at + 5;
        }
    }

    /// <summary>Tracks when the live requested target last changed, so a driver statement older than that change is not followed.</summary>
    private void NoteTarget(double? target)
    {
        if (target != _previousTarget) { _previousTarget = target; _targetChangedAt = DateTimeOffset.Now; }
    }

    /// <summary>The dashed graph line: the driver's own current rate when it has said so since the target last changed, else the requested target.</summary>
    private double? DriverFollowedTarget(StreamingHealthSnapshot health)
    {
        if (_target is null || !_vrRunning) return _target;
        var now = DateTimeOffset.Now;
        if (health.DriverBitrate is { } driver && driver.ObservedAt > _targetChangedAt && now - driver.ObservedAt <= FreshLimit) return driver.Mbps;
        return _target;
    }

    private static string Age(DateTimeOffset at)
    {
        var age = DateTimeOffset.Now - at;
        return age.TotalSeconds < 5 ? "just now" : age.TotalMinutes < 1 ? $"{age.TotalSeconds:0} s ago" : $"{age.TotalMinutes:0} min ago";
    }

    private int CurrentRefreshHz((double? Mbps, double? Fps) measured) => measured.Fps is double fps && fps >= 60 ? (int)Math.Round(fps) : CodecRefreshInput.SelectedIndex == 1 ? 120 : 90;

    private void RenderWirelessLink(StreamingHealthSnapshot health, EncoderSnapshot snapshot, (double? Mbps, double? Fps) measured)
    {
        var now = DateTimeOffset.Now;
        var measuredMbps = measured.Mbps;
        var refreshHz = CurrentRefreshHz(measured);
        LinkStateReadback.Text = health.LinkActive switch
        {
            true => $"Video link: active since {health.LinkChangedAt?.ToLocalTime():HH:mm:ss}",
            false => $"Video link: inactive since {health.LinkChangedAt?.ToLocalTime():HH:mm:ss} • the headset stopped or paused streaming. Restart Frame stream asks it to stream again.",
            null => "Video link: no change seen since this app opened"
        };
        LinkStateReadback.Foreground = new SolidColorBrush(health.LinkActive switch { true => Good, false => Amber, null => Neutral });
        var estimate = health.LatestEstimate;
        var driver = health.DriverBitrate;
        var estimateText = estimate is null
            ? "SteamVR link estimate: waiting for a stream (SteamVR logs it every 30 s)"
            : $"SteamVR link estimate: {estimate.Mbps:0} Mbps ({(estimate.Automatic == true ? "automatic bitrate" : "manual target")}) • {Age(estimate.ObservedAt)}";
        if (driver is { Source: StreamingHealthCategory.NetworkBadLink } stalled && _target is double requested && Math.Abs(stalled.Mbps - requested) >= 2.5 && now - stalled.ObservedAt <= FreshLimit)
            estimateText += $"\nSteamVR is sending at {stalled.Mbps:0} Mbps after a link stall ({Age(stalled.ObservedAt)}); the requested target is {requested:0} Mbps.";
        LinkEstimateReadback.Text = estimateText;
        var perMinute = health.ThrottleEventsLastMinute;
        HeldFramesReadback.Text = health.SenderThrottleEvents == 0 && health.LinkStalls == 0 && estimate is null
            ? "Held frames: none recorded yet"
            : $"Held frames: {perMinute} in the last minute ({health.ThrottleMsLastMinute:0} ms of delay) • {health.SenderThrottleEvents} since this app opened" +
              (health.LinkStalls > 0 ? $" • {health.LinkStalls} sender stall{(health.LinkStalls == 1 ? "" : "s")} of 1 s or more (reconnects, not counted as held frames)" : "");
        HeldFramesReadback.Foreground = new SolidColorBrush(WirelessAdvisor.HeldFramesLimiting(perMinute, health.ThrottleMsLastMinute, refreshHz) ? Bad
            : WirelessAdvisor.HeldFramesNoticeable(perMinute, health.ThrottleMsLastMinute, refreshHz) ? Amber : Neutral);
        var histogram = health.LatestHistogram;
        CapacityPanel.Visibility = histogram is null ? Visibility.Collapsed : Visibility.Visible;
        if (histogram is null)
        {
            CapacityReadback.Text = "Link capacity: waiting for SteamVR's 30-second capacity histogram.";
            CapacityAxisRight.Text = "";
        }
        else
        {
            var h = histogram.Histogram;
            var p10 = h.Percentile(0.10); var median = h.Percentile(0.5);
            CapacityReadback.Text = $"Link capacity over the last 30 s ({h.TotalSamples} frames): {h.OverflowShare:P0} at or above {h.OverflowEdgeMbps:0} Mbps • 10th percentile {(p10.AtOrAbove ? "≥" : "")}{p10.Mbps:0} Mbps • median {(median.AtOrAbove ? "≥" : "")}{median.Mbps:0} Mbps • {h.ShareBelow(100):P1} below 100 Mbps • {Age(histogram.ObservedAt)}";
            CapacityAxisRight.Text = $"≥{h.OverflowEdgeMbps:0} Mbps";
        }
        DrawCapacityGraph(histogram?.Histogram);
        var radioFresh = now - _radio.ObservedAt <= TimeSpan.FromSeconds(20) ? _radio : WirelessLinkSnapshot.Unknown("Wireless status is stale.");
        RadioReadback.Text = radioFresh.Summary;
        RadioReadback.Foreground = new SolidColorBrush(radioFresh.Weak || radioFresh.SlowPhy || radioFresh.LegacyBand || radioFresh.Narrow ? Amber : radioFresh.Connected ? Good : Neutral);
        RadioDetail.Text = radioFresh.Detail;
        ConnectionRadioStatus.Text = radioFresh.Summary + "\n" + radioFresh.Detail;

        var active = _links.ActiveLinks;
        // An active headset link alone carries no video; only encoder output or the driver's own
        // periodic link statistics prove a stream is running.
        var streamActive = _vrRunning && (snapshot.Active || estimate is not null && now - estimate.ObservedAt <= FreshLimit
            || histogram is not null && now - histogram.ObservedAt <= FreshLimit || perMinute > 0);
        var input = new WirelessAdvisorInput
        {
            CurrentTargetMbps = _target, AutomaticBitrate = _liveAutomatic == true,
            PairedCodec = snapshot.PairedCodecTrialPresent || _codecService.Live,
            DriverPatched = _patch?.IsPatched == true, DriverVerified = _patch is { IsSupported: true } || _patch?.IsPatched == true,
            UsbSlow = _usbCurrent.HasSlowConnection, Radio = radioFresh.Found ? radioFresh : null,
            Histogram = histogram?.Histogram, HistogramAgeSeconds = histogram is null ? null : (now - histogram.ObservedAt).TotalSeconds,
            ThrottleEventsLastMinute = perMinute, ThrottleMsLastMinute = health.ThrottleMsLastMinute, RefreshHz = refreshHz,
            LatestBitrateChange = health.LatestBitrateChange, BitrateChangeAgeSeconds = health.LatestBitrateChange is null ? null : (now - health.LatestBitrateChange.ObservedAt).TotalSeconds,
            DriverBitrate = driver, DriverBitrateAgeSeconds = driver is null ? null : (now - driver.ObservedAt).TotalSeconds,
            HeadsetLossRatio = active.Length > 0 ? active.Max(l => l.LossRatio) : null,
            HeadsetLatencyMs = active.Length > 0 ? active.Max(l => l.LatencyMs) : null,
            MeasuredVideoMbps = measuredMbps, StreamActive = streamActive
        };
        _advice = WirelessAdvisor.Advise(input);
        var sweep = RecentSweep();
        // A measured sweep outranks the moment-to-moment reading; the live advice stays in the reasons.
        var headline = sweep is { BestCleanMbps: int best }
            ? $"Sweep result ({Age(_sweepConcludedAt)}): {sweep.Summary} Use {best} Mbps as the measured ceiling for this setup."
            : _advice.Headline;
        AdviceHeadline.Text = headline;
        AdviceHeadline.Foreground = new SolidColorBrush(sweep is { BestCleanMbps: not null } ? Good : _advice.LinkLimited ? Amber : _advice.Headroom ? Good : Neutral);
        var reasons = sweep is not null ? _advice.Reasons.Prepend("Right now: " + _advice.Headline).ToList() : _advice.Reasons.ToList();
        AdviceReasons.Text = reasons.Count == 0 ? "" : "• " + string.Join("\n• ", reasons) + (_advice.Confidence == AdviceConfidence.None ? "" : $"\nConfidence: {_advice.Confidence.ToString().ToLowerInvariant()}. Measurements, not a guarantee of picture quality. Auto tune compares complete presets with its own test scene.");
        StreamAdviceHeadline.Text = headline;
        StreamAdviceDetail.Text = _advice.Reasons.Count == 0 ? "The Overview card explains the advice once a stream is running." : _advice.Reasons[0];
        var recommendation = EffectiveRecommendation();
        var busy = _sweepRunning || _actionPending || _benchmarkStop is not null;
        UseAdviceButton.IsEnabled = StreamUseAdviceButton.IsEnabled = recommendation is not null && !busy;
        UseAdviceButton.Content = StreamUseAdviceButton.Content = recommendation is int r ? $"Use {r} Mbps" : "Use recommended target";
        SweepButton.IsEnabled = !busy && _vrRunning && !snapshot.PairedCodecTrialPresent && !_codecService.Live;
        RestartStreamButton.IsEnabled = !busy && _vrRunning && !string.IsNullOrWhiteSpace(_preferences.SshHost);
    }

    private bool FrameSshConfigured => !string.IsNullOrWhiteSpace(_preferences.SshHost) && File.Exists(_preferences.SshKey) && File.Exists(_preferences.SshKnownHosts);

    private async void RestartFrameStream_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        await EnsureCodecStableAsync();
        if (_benchmarkStop is not null) throw new InvalidOperationException("Auto tune is running; it manages the Frame connection itself.");
        if (!FrameSshConfigured) throw new InvalidOperationException("Restarting the stream from the headset needs the paired wireless SSH connection under Connection.");
        if (!await Task.Run(() => OpenVrSettings.IsRunning)) throw new InvalidOperationException("Start SteamVR first; the headset streams to a running SteamVR.");
        var restored = await FrameStreamControl.RestartAsync(_preferences, _streamHealth, message => ShowStatus(message, false), _lifetime?.Token ?? default);
        _rate.Reset(); _quantizers.Reset(); _nextConnectionRead = _nextReceiverRead = 0;
        return restored ? "The Frame stopped and restarted its PC stream; the driver reports the video link active again."
            : "The Frame was asked to stop and restart its PC stream, but the driver has not reported the link active yet. Keep the Frame awake and check its streaming menu.";
    });

    /// <summary>Brings a dropped video link back from the headset side; returns false when it stayed down.</summary>
    private async Task<bool> RecoverLinkForSweepAsync(CancellationToken token)
    {
        if (!FrameSshConfigured) return false;
        SweepStatus.Text = "The video link is down; asking the Frame to stop and restart its PC stream…";
        try { return await FrameStreamControl.RestartAsync(_preferences, _streamHealth, message => SweepStatus.Text = message, token); }
        catch (Exception error) when (error is not OperationCanceledException) { SweepStatus.Text = "The Frame could not restart its stream: " + error.Message; return false; }
    }

    private SweepConclusion? RecentSweep() => _sweepConclusion is not null && DateTimeOffset.Now - _sweepConcludedAt <= TimeSpan.FromMinutes(20) ? _sweepConclusion : null;
    private int? EffectiveRecommendation() => RecentSweep() is { BestCleanMbps: int best } ? best : _advice.RecommendedMbps;

    private void DrawCapacityGraph(LinkCapacityHistogram? histogram)
    {
        CapacityGraph.Children.Clear();
        double width = CapacityGraph.ActualWidth, height = CapacityGraph.ActualHeight;
        if (width < 1 || height < 1 || histogram is null || histogram.TotalSamples == 0) return;
        var count = histogram.Counts.Count;
        var slot = width / count;
        var maximum = Math.Log10(1 + histogram.Counts.Max());
        if (maximum <= 0) return;
        for (var i = 0; i < count; i++)
        {
            var value = histogram.Counts[i];
            if (value == 0) continue;
            var bar = Math.Max(1.5, Math.Log10(1 + value) / maximum * height);
            var overflow = i == count - 1;
            var rectangle = new Rectangle
            {
                Width = Math.Max(1, slot - 1), Height = bar,
                Fill = new SolidColorBrush(overflow ? Good : Color.FromArgb(255, 26, 159, 255))
            };
            Canvas.SetLeft(rectangle, i * slot); Canvas.SetTop(rectangle, height - bar);
            CapacityGraph.Children.Add(rectangle);
        }
        if (_target is double target && histogram.BucketMbps > 0 && target < histogram.OverflowEdgeMbps)
        {
            var x = Math.Clamp(target / histogram.BucketMbps * slot, 0, width);
            CapacityGraph.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = height, StrokeThickness = 1, Stroke = new SolidColorBrush(Color.FromArgb(255, 140, 140, 150)), StrokeDashArray = new DoubleCollection { 3, 3 } });
        }
    }
    private void CapacityGraph_SizeChanged(object sender, SizeChangedEventArgs e) => DrawCapacityGraph(_streamHealth.Current.LatestHistogram?.Histogram);

    private void UseAdvice_Click(object sender, RoutedEventArgs e)
    {
        if (_actionPending || _sweepRunning || _benchmarkStop is not null) return;
        if (EffectiveRecommendation() is not int mbps) { ShowStatus("No recommendation is available yet. Start a wireless stream and wait for SteamVR's link measurements.", true); return; }
        if (SelectedCodec != StreamCodec.Hevc) { ShowStatus("The recommendation is for the HEVC stream. Select HEVC first.", true); return; }
        BitrateInput.Value = mbps;
        AutomaticInput.IsOn = false;
        _bandwidthEdited = true; _basicEditRevision++; UpdateStreamEditStatus();
        SectionRequested?.Invoke("stream");
        ShowStatus($"{mbps} Mbps loaded into Stream settings with automatic bitrate off. Review the controls and click Apply stream settings.", false);
    }

    private async void Sweep_Click(object sender, RoutedEventArgs e)
    {
        if (_actionPending || _sweepRunning) return;
        try
        {
            if (_benchmarkStop is not null) throw new InvalidOperationException("Auto tune is running. Wait for it to finish or stop it before measuring the link.");
            await EnsureCodecStableAsync();
            if (!await Task.Run(() => OpenVrSettings.IsRunning)) throw new InvalidOperationException("Start SteamVR and a wireless stream before measuring the link.");
            if (SelectedCodec != StreamCodec.Hevc || _codecService.Live) throw new InvalidOperationException("The capacity sweep measures the HEVC stream. End the codec session first.");
            if (_bandwidthEdited || _codecEdited || _pendingPreset != null) throw new InvalidOperationException("Apply or reload the pending Stream settings edits first; the sweep changes the live target and restores it afterwards.");
            var health = _streamHealth.Current;
            if (health.LinkActive == false && FrameSshConfigured)
            {
                // The headset paused or stopped streaming: do what the user would do on the headset, stop and stream again.
                ShowStatus("The video link is down; asking the Frame to stop and restart its PC stream before the sweep…", false);
                if (!await FrameStreamControl.RestartAsync(_preferences, _streamHealth, message => ShowStatus(message, false), _lifetime?.Token ?? default))
                    throw new InvalidOperationException("The Frame was asked to restart its PC stream, but the video link did not come back. Keep the Frame awake and check its streaming menu.");
                health = _streamHealth.Current;
            }
            var streaming = health.LinkActive == true || _links.ActiveLinks.Length > 0 || _monitorInstalled || health.LatestEstimate is { } est && DateTimeOffset.Now - est.ObservedAt <= FreshLimit;
            if (!streaming) throw new InvalidOperationException("No active stream was detected. Put the headset on, start streaming, and wait for the first link estimate (about 30 s).");
            var live = await Task.Run(() => OpenVrSettings.Read(_paths));
            var ceiling = _patch?.IsPatched == true ? WirelessAdvisor.PatchedCeilingMbps : WirelessAdvisor.StockCeilingMbps;
            var start = (int)Math.Clamp(Math.Round(live.Target * 0.5 / 50) * 50, 100, ceiling);
            var plan = LinkCapacitySweep.Plan(start, ceiling);
            var minutes = plan.Count * (LinkCapacitySweep.SettleTime + LinkCapacitySweep.MeasureTime).TotalMinutes;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Measure the link's limit?", PrimaryButtonText = "Start sweep", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 440,
                    Text = $"The app will set the manual target to {string.Join(", ", plan)} Mbps in turn, holding each for {(LinkCapacitySweep.SettleTime + LinkCapacitySweep.MeasureTime).TotalSeconds:0} s while it counts held frames, link stalls, headset loss and recovery requests (up to {minutes:0} minutes). Keep playing or moving in the headset so the encoder has real work. Your current target ({live.Target:0} Mbps, automatic {(live.Automatic ? "on" : "off")}) is restored when the sweep ends or is stopped. For a full comparison of widths, Hz, passes and gaze offsets, use Auto tune instead." +
                        (ceiling == WirelessAdvisor.StockCeilingMbps ? " The stock driver limits the sweep to 232 Mbps." : "")
                }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            _sweep = new SweepRun { Plan = plan, OriginalTarget = live.Target, OriginalAutomatic = live.Automatic };
            _sweepCancellation?.Dispose();
            _sweepCancellation = new();
            _sweepRunning = true;
            SweepStopButton.Visibility = Visibility.Visible;
            SweepResultsCard.Visibility = Visibility.Visible;
            SweepResults.Text = "";
            RefreshCodecButtons();
            _sweepTask = RunSweepAsync(_sweep, _sweepCancellation.Token);
            await _sweepTask;
        }
        catch (Exception error) when (error is not OperationCanceledException) { ShowStatus(error.Message, true); }
    }

    private async void StopSweep_Click(object sender, RoutedEventArgs e) => await StopSweepAsync();

    private async Task StopSweepAsync()
    {
        if (_sweepTask is null) return;
        _sweepCancellation?.Cancel();
        try { await _sweepTask; } catch (Exception) { }
    }

    private async Task ApplySweepTargetAsync(double target, bool automatic)
    {
        await _actions.WaitAsync();
        try { await Task.Run(() => OpenVrSettings.Apply(_paths, new Dictionary<string, object?> { ["automaticBandwidth"] = automatic, ["targetBandwidth"] = target })); }
        finally { _actions.Release(); }
    }

    private async Task RunSweepAsync(SweepRun run, CancellationToken token)
    {
        var outcome = "Sweep ended.";
        var recoveries = 0;
        try
        {
            for (run.Index = 0; run.Index < run.Plan.Count; run.Index++)
            {
                token.ThrowIfCancellationRequested();
                var target = run.CurrentTarget;
                var applied = _streamHealth.Current;
                run.BaseChanges = applied.BitrateChanges; run.LowestDriver = null;
                run.StepStarted = DateTimeOffset.Now;
                await ApplySweepTargetAsync(target, false);
                run.Measuring = false;
                SweepStatus.Text = $"Step {run.Index + 1} of {run.Plan.Count}: {target} Mbps, settling for {LinkCapacitySweep.SettleTime.TotalSeconds:0} s…";
                await Task.Delay(LinkCapacitySweep.SettleTime, token);
                var health = _streamHealth.Current; var receiver = _receiver.Current; var compositor = _lastCompositor;
                run.BaseThrottles = health.SenderThrottleEvents; run.BaseThrottleMs = health.SenderThrottleMs; run.BaseBadLinks = health.NetworkBadLinkEvents;
                run.BaseResets = health.StreamResetRequests; run.BaseRetries = receiver.RetryEvents;
                run.BaseDrops = compositor?.DroppedFrames ?? 0;
                run.MaxLoss = run.MaxLatency = null; run.Video.Clear(); run.Fps.Clear();
                // A step-down the driver made while settling belongs to this target too.
                if (health.LatestBitrateChange is { ChangedBitrate: true } settled && settled.ObservedAt >= run.StepStarted && settled.NewBitrateMbps < settled.PreviousBitrateMbps)
                    run.LowestDriver = settled.NewBitrateMbps;
                run.MeasureStarted = DateTimeOffset.Now; run.Measuring = true;
                SweepStatus.Text = $"Step {run.Index + 1} of {run.Plan.Count}: {target} Mbps, measuring for {LinkCapacitySweep.MeasureTime.TotalSeconds:0} s…";
                var dropped = false;
                for (var elapsed = TimeSpan.Zero; elapsed < LinkCapacitySweep.MeasureTime; elapsed += TimeSpan.FromSeconds(1))
                {
                    await Task.Delay(1000, token);
                    if (_streamHealth.Current.LinkActive == false) { dropped = true; break; }
                }
                run.Measuring = false;
                if (dropped)
                {
                    // Not a bitrate verdict: the headset stopped streaming. Recover once per step, then retry the same step.
                    run.Results.Add(new(target, BuildSample(run), SweepVerdict.Failed, "The video link dropped during the step; it was not scored."));
                    RenderSweepResults(run);
                    if (recoveries < 2 && await RecoverLinkForSweepAsync(token))
                    {
                        recoveries++;
                        run.Results.RemoveAt(run.Results.Count - 1);
                        run.Index--;
                        continue;
                    }
                    outcome = "Sweep ended early: the video link dropped and could not be restarted from the headset.";
                    break;
                }
                var stepHz = run.Fps.Count > 0 ? (int)Math.Round(run.Fps.Average()) : CodecRefreshInput.SelectedIndex == 1 ? 120 : 90;
                var result = LinkCapacitySweep.Evaluate(target, BuildSample(run), stepHz);
                run.Results.Add(result);
                RenderSweepResults(run);
                if (LinkCapacitySweep.ShouldStop(run.Results)) break;
            }
            if (outcome == "Sweep ended.") outcome = "Sweep finished.";
        }
        catch (OperationCanceledException) { outcome = "Sweep stopped."; }
        catch (Exception error) { outcome = "Sweep ended early: " + error.Message; }
        finally
        {
            run.Measuring = false;
            string restore;
            try { await ApplySweepTargetAsync(run.OriginalTarget, run.OriginalAutomatic); restore = $" Restored {run.OriginalTarget:0} Mbps with automatic bitrate {(run.OriginalAutomatic ? "on" : "off")}."; }
            catch (Exception error) { restore = " The original target could not be restored: " + error.Message + " Check Stream settings."; }
            _sweepRunning = false;
            _sweepTask = null;
            SweepStopButton.Visibility = Visibility.Collapsed;
            if (run.Results.Count > 0)
            {
                _sweepConclusion = LinkCapacitySweep.Conclude(run.Results);
                _sweepConcludedAt = DateTimeOffset.Now;
                SweepStatus.Text = _sweepConclusion.Summary + restore;
                RenderSweepResults(run);
            }
            else SweepStatus.Text = outcome + restore;
            RefreshCodecButtons();
            ShowStatus(outcome + (run.Results.Count > 0 ? " " + _sweepConclusion!.Summary : "") + restore, false);
        }
    }

    private SweepSample BuildSample(SweepRun run)
    {
        var health = _streamHealth.Current; var receiver = _receiver.Current; var compositor = _lastCompositor;
        var seconds = (DateTimeOffset.Now - run.MeasureStarted).TotalSeconds;
        return new(seconds,
            Math.Max(0, health.SenderThrottleEvents - run.BaseThrottles), Math.Max(0, health.SenderThrottleMs - run.BaseThrottleMs),
            Math.Max(0, health.NetworkBadLinkEvents - run.BaseBadLinks), Math.Max(0, health.BitrateChanges - run.BaseChanges),
            run.LowestDriver, run.MaxLoss, run.MaxLatency,
            Math.Max(0, (compositor?.DroppedFrames ?? run.BaseDrops) - run.BaseDrops),
            run.Video.Count > 0 ? run.Video.Average() : null, run.Fps.Count > 0 ? run.Fps.Average() : null,
            Math.Max(0, receiver.RetryEvents - run.BaseRetries), Math.Max(0, health.StreamResetRequests - run.BaseResets));
    }

    /// <summary>Called from each poll so a step sees the same readings the dashboard shows.</summary>
    private void ObserveSweep(StreamingHealthSnapshot health, (double? Mbps, double? Fps) measured)
    {
        if (_sweep is not { Measuring: true } run) return;
        if (measured.Mbps is double mbps) run.Video.Add(mbps);
        if (measured.Fps is double fps) run.Fps.Add(fps);
        foreach (var link in _links.ActiveLinks)
        {
            run.MaxLoss = Math.Max(run.MaxLoss ?? 0, link.LossRatio);
            run.MaxLatency = Math.Max(run.MaxLatency ?? 0, link.LatencyMs);
        }
        if (health.DriverBitrate is { } driver && driver.ObservedAt >= run.StepStarted)
            run.LowestDriver = Math.Min(run.LowestDriver ?? driver.Mbps, driver.Mbps);
    }

    private void RenderSweepResults(SweepRun run)
    {
        var lines = run.Results.Select(r =>
            $"{r.TargetMbps,4} Mbps  {r.Verdict,-8}  {r.ThrottlesPerMinute,5:0.0} held/min  {(r.Sample.MeanVideoMbps is double v ? $"{v,5:0} Mbps video" : "  video n/a  ")}  {(r.Sample.MaxLossRatio is double l ? $"{l,6:P2} loss" : " loss n/a ")}  {r.Reason}");
        SweepResults.Text = string.Join("\n", lines);
    }
}
