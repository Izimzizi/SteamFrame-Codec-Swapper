using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SteamLinkVRSettings.Frame;
using SteamLinkVRSettings.Services;
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
    private FrameAwakeKeeper? _awakeByToggle;
    private bool _syncingAwake;

    /// <summary>Starts a headset activity lease for an action; null when SSH is not paired or the Frame refused it.</summary>
    private async Task<FrameAwakeKeeper?> HoldFrameAwakeAsync(Action<string> status, CancellationToken token)
    {
        if (!FrameSshConfigured) return null;
        if (_awakeByToggle is { Active: true }) return null; // The toggle already holds one.
        var keeper = FrameAwakeKeeper.ForFrame(_preferences);
        try { await keeper.StartAsync(token); return keeper; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await keeper.DisposeAsync();
            status("Could not keep the Frame awake: " + error.Message + " If the headset is off your head it may pause streaming.");
            return null;
        }
    }

    private async void KeepFrameAwake_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingAwake) return;
        if (KeepFrameAwakeInput.IsOn)
        {
            if (_awakeByToggle is { Active: true }) return;
            var keeper = FrameSshConfigured ? FrameAwakeKeeper.ForFrame(_preferences) : null;
            try
            {
                if (keeper is null) throw new InvalidOperationException("Keeping the Frame awake needs the paired wireless SSH connection under Connection.");
                await keeper.StartAsync(_lifetime?.Token ?? default);
                _awakeByToggle = keeper;
                ShowStatus("The Frame's activity extension is being renewed every 20 seconds. It stops when this is switched off or the app closes.", false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (keeper is not null) await keeper.DisposeAsync();
                _syncingAwake = true;
                try { KeepFrameAwakeInput.IsOn = false; } finally { _syncingAwake = false; }
                ShowStatus("Could not keep the Frame awake: " + error.Message, true);
            }
        }
        else await ReleaseFrameAwakeAsync();
    }

    private async Task ReleaseFrameAwakeAsync()
    {
        var keeper = _awakeByToggle;
        _awakeByToggle = null;
        if (keeper is not null) await keeper.DisposeAsync();
        if (KeepFrameAwakeInput.IsOn)
        {
            _syncingAwake = true;
            try { KeepFrameAwakeInput.IsOn = false; } finally { _syncingAwake = false; }
        }
    }

    private sealed class SweepRun
    {
        public required IReadOnlyList<int> Plan;
        public int Index;
        public readonly List<SweepStepResult> Results = [];
        public double OriginalTarget;
        public bool OriginalAutomatic;
        public bool ApplyWhenDone, UseTestScene, QuickCheck;
        public BenchmarkSceneProcess? Scene;
        public string SceneNote = "";
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

    private int CurrentRefreshHz((double? Mbps, double? Fps) measured) => measured.Fps is double fps && fps >= 60 ? (int)Math.Round(fps) : LegacyRefreshHz;

    private double? _lastVideoMbps;

    private void RenderWirelessLink(StreamingHealthSnapshot health, EncoderSnapshot snapshot, (double? Mbps, double? Fps) measured)
    {
        _lastVideoMbps = measured.Mbps;
        var now = DateTimeOffset.Now;
        var measuredMbps = measured.Mbps;
        var refreshHz = CurrentRefreshHz(measured);
        LinkStateReadback.Text = health.LinkActive switch
        {
            true => $"Video link: active since {health.LinkChangedAt?.ToLocalTime():HH:mm:ss}",
            false => $"Video link: inactive since {health.LinkChangedAt?.ToLocalTime():HH:mm:ss} • the headset stopped or paused streaming. Restart Frame stream asks it to stream again.",
            null => "Video link: no change seen since this app opened"
        } + (_awakeByToggle is { Active: true } awake ? $" • keeping the Frame awake ({awake.Renewals} renewals{(awake.LastError is null ? "" : ", last renewal failed")})" : "");
        LinkStateReadback.Foreground = new SolidColorBrush(health.LinkActive switch { true => Good, false => Amber, null => Neutral });
        KeepFrameAwakeInput.IsEnabled = FrameSshConfigured && !_actionPending;
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
        var streamActive = _vrRunning && (measured.Fps is > 0 || health.LinkActive != false &&
            (estimate is not null && now - estimate.ObservedAt <= FreshLimit || histogram is not null && now - histogram.ObservedAt <= FreshLimit));
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
            HeadsetLossRatio = streamActive && active.Length > 0 ? active.Max(l => l.LossRatio) : null,
            HeadsetLatencyMs = streamActive && active.Length > 0 ? active.Max(l => l.LatencyMs) : null,
            MeasuredVideoMbps = measuredMbps, StreamActive = streamActive
        };
        _advice = WirelessAdvisor.Advise(input);
        var sweep = RecentSweep();
        var sweepSettling = sweep is not null && now - _sweepConcludedAt < TimeSpan.FromSeconds(90);
        var preferredCeiling = WirelessAdvisor.PreferredSweepCeiling(_advice, sweep, sweepSettling);
        var headline = sweep is { Check: true } && sweepSettling
            ? $"{sweep.Summary} " + (sweep.BestCleanMbps is int ? "Keep this rate." : sweep.HighestMarginalMbps is int ? "Step down a notch if you see held frames, or sweep again later." : "Step down now, or run Find my link's limit.")
            : preferredCeiling is int best
            ? $"Sweep result ({Age(_sweepConcludedAt)}): {sweep!.Summary} Use {best} Mbps as the measured ceiling for this setup ({ValveComparison(best)})."
            : sweep is { BestCleanMbps: null } && sweepSettling
                // No clean step: the sweep still outranks the live reading, and the advice is to hold back, not to try harder.
                ? $"Sweep result ({Age(_sweepConcludedAt)}): {sweep.Summary}" + (sweep.HighestMarginalMbps is int m
                    ? $" Nothing above {m} Mbps is worth trying now; if the current rate shows held frames, step down to {m} Mbps or lower and sweep again later."
                    : " Check the dongle position and the radio signal, then sweep again.")
                : _advice.Headline;
        AdviceHeadline.Text = headline;
        AdviceHeadline.Foreground = new SolidColorBrush(preferredCeiling is not null ? Good : sweepSettling && sweep is { Check: true, HighestMarginalMbps: not null } ? Amber : sweepSettling && sweep is { Check: true, BestCleanMbps: null } ? Bad : _advice.LinkLimited ? Amber : _advice.Headroom ? Good : Neutral);
        // For a minute after a sweep the live counters still hold its top steps, so the moment-to-moment advice would argue with the result.
        var reasons = sweepSettling ? new List<string> { sweep is { Check: true } ? "Right now: the last minute still includes the check; live advice resumes in a moment." : "Right now: the last minute still includes the sweep's highest steps; live advice resumes in a moment." }
            : sweep is not null ? _advice.Reasons.Prepend("Right now: " + _advice.Headline).ToList() : _advice.Reasons.ToList();
        var context = new List<string>();
        if (sweep is not null && preferredCeiling is null)
            context.Add($"Recent {(sweep.Check ? "check" : "sweep")} ({Age(_sweepConcludedAt)}): {sweep.Summary}" +
                (sweep.LightlyTested ? " The target was not fully tested; it is not offered as a measured ceiling." :
                 !sweep.Check && !sweep.Confirmed ? " This result has no completed clean confirmation; sweep again before applying it." : " Current live advice takes priority."));
        if (sweep is null && _preferences.LastSweepCeilingMbps is int earlier && _preferences.LastSweepAt is { } at)
            context.Add($"Last measured ceiling: {earlier} Mbps on {at.ToLocalTime():MMM d HH:mm} with {_preferences.LastSweepRefreshHz} fps delivered ({ValveComparison(earlier)}); it is saved as the Measured ceiling preset in Stream settings. Sweep again before relying on it, and after changing the dongle position, headset refresh rate or game.");
        else if (sweep is null && _preferences.LastSweepCeilingMbps is null && _preferences.LastSweepAt is { } last && _preferences.LastSweepSummary.Length > 0)
            context.Add($"Last sweep on {last.ToLocalTime():MMM d HH:mm}: {_preferences.LastSweepSummary} Sweep again before trusting a rate.");
        // Several sweeps say more than one: a link that measures differently from hour to hour needs different advice.
        if (LinkCapacitySweep.DescribeHistory(_preferences.SweepHistory, now) is string history) context.Add(history);
        reasons.InsertRange(0, context);
        AdviceReasons.Text = reasons.Count == 0 ? "" : "• " + string.Join("\n• ", reasons) + (_advice.Confidence == AdviceConfidence.None ? "" : $"\nConfidence: {_advice.Confidence.ToString().ToLowerInvariant()}. Measurements, not a guarantee of picture quality. Auto tune compares complete presets with its own test scene.");
        StreamAdviceHeadline.Text = headline;
        StreamAdviceDetail.Text = _advice.Reasons.Count == 0 ? "The Overview card explains the advice once a stream is running." : _advice.Reasons[0];
        var recommendation = EffectiveRecommendation();
        var busy = _sweepRunning || _actionPending || _benchmarkStop is not null;
        UseAdviceButton.IsEnabled = StreamUseAdviceButton.IsEnabled = recommendation is not null && !busy;
        UseAdviceButton.Content = StreamUseAdviceButton.Content = recommendation is int r ? $"Use {r} Mbps" : "Use recommended target";
        // One click to run at what the link was shown to carry: the measured ceiling, live, with automatic bitrate off.
        var ceiling = MeasuredCeiling();
        ApplyCeilingButton.Visibility = ceiling is int ? Visibility.Visible : Visibility.Collapsed;
        ApplyCeilingButton.Content = ceiling is int c ? (_target is double t && Math.Abs(t - c) < 0.5 && _liveAutomatic == false ? $"Running at measured ceiling ({c} Mbps)" : $"Apply {c} Mbps now") : "Apply measured ceiling";
        ApplyCeilingButton.IsEnabled = ceiling is int c2 && !busy && _vrRunning && !(_target is double t2 && Math.Abs(t2 - c2) < 0.5 && _liveAutomatic == false) && !snapshot.PairedCodecTrialPresent && !_codecService.Live;
        // A link that measured differently from day to day is better served by automatic mode starting high.
        var autoStart = LinkCapacitySweep.AutomaticStart(_preferences.SweepHistory, now);
        var autoActive = autoStart is int a && _liveAutomatic == true && Math.Abs(StartingBandwidthInput.Value - a) < 0.5;
        AutoStartButton.Visibility = autoStart is int ? Visibility.Visible : Visibility.Collapsed;
        AutoStartButton.Content = autoStart is int s0 ? (autoActive ? $"Automatic from {s0} Mbps (active)" : $"Automatic from {s0} Mbps") : "Automatic from measured";
        AutoStartButton.IsEnabled = autoStart is int && !busy && _vrRunning && !autoActive && !snapshot.PairedCodecTrialPresent && !_codecService.Live;
        SweepButton.IsEnabled = QuickCheckButton.IsEnabled = !busy && _vrRunning && !snapshot.PairedCodecTrialPresent && !_codecService.Live;
        RestartStreamButton.IsEnabled = !busy && _vrRunning && !string.IsNullOrWhiteSpace(_preferences.SshHost);
    }

    private bool FrameSshConfigured => !string.IsNullOrWhiteSpace(_preferences.SshHost) && File.Exists(_preferences.SshKey) && File.Exists(_preferences.SshKnownHosts);

    private async void RestartFrameStream_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        await EnsureCodecStableAsync();
        if (_benchmarkStop is not null) throw new InvalidOperationException("Auto tune is running; it manages the Frame connection itself.");
        if (!FrameSshConfigured) throw new InvalidOperationException("Restarting the stream from the headset needs the paired wireless SSH connection under Connection.");
        if (!await Task.Run(() => OpenVrSettings.IsRunning)) throw new InvalidOperationException("Start SteamVR first; the headset streams to a running SteamVR.");
        // Hold the headset's activity extension through the restart; an off-head Frame would otherwise idle again at once.
        await using var awake = await HoldFrameAwakeAsync(message => ShowStatus(message, false), _lifetime?.Token ?? default);
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
    /// <summary>
    /// The ceiling worth a one-click Apply: a sweep that finished in the last 20 minutes with the encoder near the
    /// target and a completed clean confirmation. Current live trouble takes precedence after settling.
    /// </summary>
    private int? MeasuredCeiling() => WirelessAdvisor.PreferredSweepCeiling(_advice, RecentSweep(), DateTimeOffset.Now - _sweepConcludedAt < TimeSpan.FromSeconds(90));

    private async void ApplyCeiling_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        await EnsureCodecStableAsync();
        if (MeasuredCeiling() is not int ceiling) throw new InvalidOperationException("Run Find my link's limit first; a ceiling is applied from a sweep that just finished with the encoder near the target. Older results are in the Measured ceiling preset.");
        if (SelectedCodec != StreamCodec.Hevc || _codecService.Live) throw new InvalidOperationException("The measured ceiling applies to the HEVC stream. End the codec session first.");
        if (_bandwidthEdited || _codecEdited || _pendingPreset != null) throw new InvalidOperationException("Apply or reload the pending Stream settings edits first.");
        return await ApplyMeasuredCeilingAsync(ceiling);
    });

    private async void AutomaticStart_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        await EnsureCodecStableAsync();
        if (LinkCapacitySweep.AutomaticStart(_preferences.SweepHistory, DateTimeOffset.Now) is not int start) throw new InvalidOperationException("The last week's sweeps agree; use the measured ceiling instead.");
        if (SelectedCodec != StreamCodec.Hevc || _codecService.Live) throw new InvalidOperationException("Automatic bitrate applies to the HEVC stream. End the codec session first.");
        if (_bandwidthEdited || _codecEdited || _pendingPreset != null) throw new InvalidOperationException("Apply or reload the pending Stream settings edits first.");
        var note = await CheckBitrateCeilingAsync(start);
        // Start where the best sweep got to and let SteamVR step down; the manual target is raised so it does not cap the start.
        var target = Math.Max(_target ?? start, start);
        var values = new Dictionary<string, object?> { ["automaticBandwidth"] = true, ["recommendedBandwidthMbit"] = start, ["targetBandwidth"] = target };
        if (await Task.Run(() => OpenVrSettings.IsRunning)) await Task.Run(() => OpenVrSettings.Apply(_paths, values));
        else await new SettingsService().SaveUserOverridesAsync(values);
        _syncingBandwidth = true;
        try { AutomaticInput.IsOn = true; StartingBandwidthInput.Value = start; BitrateInput.Value = target; }
        finally { _syncingBandwidth = false; }
        _target = target; _liveAutomatic = true; NoteTarget(target);
        return $"Automatic bitrate is on, starting at {start} Mbps (Valve starts at {WirelessAdvisor.ValveDefaultStartMbps}). SteamVR steps down on link trouble and the card shows the rate it settles at; sweep again on a quiet day to raise the start." + note;
    });

    /// <summary>Puts the measured ceiling on the stream (or saves it for the next session) with automatic bitrate off, and keeps the editor in step.</summary>
    private async Task<string> ApplyMeasuredCeilingAsync(int ceiling)
    {
        var note = await CheckBitrateCeilingAsync(ceiling);
        var values = new Dictionary<string, object?> { ["automaticBandwidth"] = false, ["targetBandwidth"] = (double)ceiling };
        if (await Task.Run(() => OpenVrSettings.IsRunning)) await Task.Run(() => OpenVrSettings.Apply(_paths, values));
        else await new SettingsService().SaveUserOverridesAsync(values);
        _preferences.CodecTargetMbps = ceiling; _preferences.RequestedCodec = StreamCodec.Hevc;
        await _preferences.SaveAsync();
        _syncingBandwidth = true;
        try { BitrateInput.Value = ceiling; AutomaticInput.IsOn = false; }
        finally { _syncingBandwidth = false; }
        _target = ceiling; _liveAutomatic = false; NoteTarget(ceiling);
        var caveat = _sweepConclusion is { LightlyTested: true, CarriedVideoMbps: double carried }
            ? $" The sweep's video averaged only {carried:0} Mbps at that step, so a heavier game is the real test."
            : " Watch held frames; the sweep measured this link with the scene that was running, and a heavier game can need less.";
        return $"Streaming at the measured ceiling of {ceiling} Mbps with automatic bitrate off." + caveat + note;
    }
    private static string ValveComparison(int mbps) => mbps == WirelessAdvisor.ValveDefaultStartMbps ? "the same as Valve's 200 Mbps automatic start"
        : mbps > WirelessAdvisor.ValveDefaultStartMbps ? $"{(mbps - WirelessAdvisor.ValveDefaultStartMbps) * 100 / WirelessAdvisor.ValveDefaultStartMbps}% above Valve's 200 Mbps automatic start"
        : $"{(WirelessAdvisor.ValveDefaultStartMbps - mbps) * 100 / WirelessAdvisor.ValveDefaultStartMbps}% below Valve's 200 Mbps automatic start";
    private int? EffectiveRecommendation() => MeasuredCeiling() ?? _advice.RecommendedMbps;

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

    /// <summary>
    /// What a sweep and a quick check both need: no Auto tune, a stable codec, SteamVR up, nothing pending in the
    /// editor, and a live video link (restarted from the headset when it idled). Returns whether the link was down.
    /// </summary>
    private async Task<(StreamingHealthSnapshot Health, bool WasDown)> PrepareLinkMeasurementAsync()
    {
        if (_benchmarkStop is not null) throw new InvalidOperationException("Auto tune is running. Wait for it to finish or stop it before measuring the link.");
        await EnsureCodecStableAsync();
        if (!await Task.Run(() => OpenVrSettings.IsRunning)) throw new InvalidOperationException("Start SteamVR and a wireless stream before measuring the link.");
        if (SelectedCodec != StreamCodec.Hevc || _codecService.Live) throw new InvalidOperationException("The capacity sweep measures the HEVC stream. End the codec session first.");
        if (_bandwidthEdited || _codecEdited || _pendingPreset != null) throw new InvalidOperationException("Apply or reload the pending Stream settings edits first; the sweep changes the live target and restores it afterwards.");
        var health = _streamHealth.Current;
        // A link the sweep has to bring back itself means the headset was idle: nobody is playing.
        var wasDown = health.LinkActive == false;
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
        return (health, wasDown);
    }

    private void StartSweepRun(SweepRun run)
    {
        _sweep = run;
        _sweepCancellation?.Dispose();
        _sweepCancellation = new();
        _sweepRunning = true;
        SweepStopButton.Visibility = Visibility.Visible;
        SweepResultsCard.Visibility = Visibility.Visible;
        SweepResults.Text = "";
        RefreshCodecButtons();
        _sweepTask = RunSweepAsync(run, _sweepCancellation.Token);
    }

    /// <summary>Is the current rate still clean today? One step at the live target, nothing changed or saved.</summary>
    private async void QuickCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_actionPending || _sweepRunning) return;
        try
        {
            var (health, wasDown) = await PrepareLinkMeasurementAsync();
            var live = await Task.Run(() => OpenVrSettings.Read(_paths));
            var target = (int)Math.Clamp(Math.Round(live.Target), 25, WirelessAdvisor.PatchedCeilingMbps);
            var idle = wasDown || health.LinkActive != true || (_lastVideoMbps ?? 0) < 20;
            StartSweepRun(new SweepRun { Plan = [target], OriginalTarget = live.Target, OriginalAutomatic = live.Automatic, UseTestScene = idle, QuickCheck = true });
            await _sweepTask!;
        }
        catch (Exception error) when (error is not OperationCanceledException) { ShowStatus(error.Message, true); }
    }

    private async void Sweep_Click(object sender, RoutedEventArgs e)
    {
        if (_actionPending || _sweepRunning) return;
        try
        {
            var (health, wasDown) = await PrepareLinkMeasurementAsync();
            var live = await Task.Run(() => OpenVrSettings.Read(_paths));
            var ceiling = _patch?.IsPatched == true ? WirelessAdvisor.PatchedCeilingMbps : WirelessAdvisor.StockCeilingMbps;
            var start = (int)Math.Clamp(Math.Round(live.Target * 0.5 / 50) * 50, 100, ceiling);
            var plan = LinkCapacitySweep.Plan(start, ceiling);
            var minutes = (plan.Count + LinkCapacitySweep.ExtraPasses) * (LinkCapacitySweep.SettleTime + LinkCapacitySweep.MeasureTime).TotalMinutes;
            var applyWhenDone = new CheckBox { Content = "Apply the measured ceiling when the sweep finishes (automatic bitrate off)", IsChecked = _preferences.SweepApplyWhenDone, Margin = new Thickness(0, 12, 0, 0) };
            // A headset on the desk shows a still scene that fills a third of any target; Auto tune's moving scene gives the encoder real work.
            var idle = wasDown || health.LinkActive != true || (_lastVideoMbps ?? 0) < 20;
            var testScene = new CheckBox { Content = "Show the moving test scene in the headset during the sweep (recommended for a headset on the desk; it takes over the view from a running VR app, so leave it off while you play)", IsChecked = idle, Margin = new Thickness(0, 4, 0, 0) };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Measure the link's limit?", PrimaryButtonText = "Start sweep", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel
                {
                    MaxWidth = 440,
                    Children =
                    {
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = $"The app will set the manual target to {string.Join(", ", plan)} Mbps in turn, holding each for {(LinkCapacitySweep.SettleTime + LinkCapacitySweep.MeasureTime).TotalSeconds:0} s while it counts held frames, link stalls, headset loss and recovery requests. A marginal or failed step is measured a second time before it counts, and the winning step gets a confirmation pass before it is saved (up to {minutes:0} minutes in all). Keep playing or moving in the headset so the encoder has real work. Your current target ({live.Target:0} Mbps, automatic {(live.Automatic ? "on" : "off")}) is restored when the sweep ends or is stopped, unless you let it apply the result. For a full comparison of widths, Hz, passes and gaze offsets, use Auto tune instead." +
                                (ceiling == WirelessAdvisor.StockCeilingMbps ? " The stock driver limits the sweep to 232 Mbps." : "")
                        },
                        applyWhenDone,
                        testScene
                    }
                }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var applyChosen = applyWhenDone.IsChecked == true;
            if (_preferences.SweepApplyWhenDone != applyChosen) { _preferences.SweepApplyWhenDone = applyChosen; try { await _preferences.SaveAsync(); } catch (Exception) { } }
            StartSweepRun(new SweepRun { Plan = plan, OriginalTarget = live.Target, OriginalAutomatic = live.Automatic, ApplyWhenDone = applyChosen, UseTestScene = testScene.IsChecked == true });
            await _sweepTask!;
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
        var recoveries = 0; var rechecks = 0;
        // Keep the headset streaming for the whole sweep when it is off the user's head.
        await using var awake = await HoldFrameAwakeAsync(message => SweepStatus.Text = message, token);
        try
        {
            if (await EnsureSweepSceneAsync(run, token))
            {
                // Switching SteamVR to the scene app made the Frame reset its stream once in testing; settle and
                // bring the link back before the first step rather than measuring into the drop.
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                if (_streamHealth.Current.LinkActive == false && recoveries < 2 && await RecoverLinkForSweepAsync(token)) recoveries++;
            }
            // A stream that just (re)started asks for resets for a while: in testing the first window after a restart
            // failed on them three times out of four. Wait for a quiet stretch before scoring anything.
            await WaitForQuietLinkAsync(token);
            for (run.Index = 0; run.Index < run.Plan.Count; run.Index++)
            {
                token.ThrowIfCancellationRequested();
                var target = run.CurrentTarget;
                var label = run.QuickCheck ? $"Checking {target} Mbps" : $"Step {run.Index + 1} of {run.Plan.Count}: {target} Mbps";
                var result = await MeasureStepAsync(run, target, label, token);
                if (result is not null && LinkCapacitySweep.NeedsRecheck(result, rechecks))
                {
                    // One bad window is more often a burst of interference than the limit: measure the same target again.
                    rechecks++;
                    RenderSweepResults(run, pending: result);
                    var second = await MeasureStepAsync(run, target, $"{label} looked marginal, re-checking it", token, reapply: false);
                    result = second is null ? null : LinkCapacitySweep.Recheck(result, second);
                }
                if (result is null)
                {
                    // Not a bitrate verdict: the headset stopped streaming. Recover once per step, then retry the same step.
                    run.Results.Add(new(target, BuildSample(run), SweepVerdict.Failed, "The video link dropped during the step; it was not scored."));
                    RenderSweepResults(run);
                    if (recoveries < 2 && await RecoverLinkForSweepAsync(token))
                    {
                        recoveries++;
                        run.Results.RemoveAt(run.Results.Count - 1);
                        RenderSweepResults(run);
                        await EnsureSweepSceneAsync(run, token);
                        run.Index--;
                        continue;
                    }
                    outcome = "Sweep ended early: the video link dropped and could not be restarted from the headset.";
                    break;
                }
                run.Results.Add(result);
                RenderSweepResults(run);
                if (LinkCapacitySweep.ShouldStop(run.Results)) break;
            }
            // The step about to become the measured ceiling gets a second pass before it is saved and recommended.
            while (!run.QuickCheck && outcome == "Sweep ended." && LinkCapacitySweep.Conclude(run.Results).BestCleanMbps is int best)
            {
                var index = run.Results.FindLastIndex(r => r.TargetMbps == best && r.Verdict == SweepVerdict.Clean);
                if (index < 0 || run.Results[index].Passes >= 2) break;
                var confirmation = await MeasureStepAsync(run, best, $"Confirming {best} Mbps with a second pass", token);
                if (confirmation is null)
                {
                    outcome = "Sweep ended early: the video link dropped during confirmation.";
                    break;
                }
                run.Results[index] = LinkCapacitySweep.Confirm(run.Results[index], confirmation);
                RenderSweepResults(run);
            }
            if (outcome == "Sweep ended.") outcome = run.QuickCheck ? "Check finished." : "Sweep finished.";
        }
        catch (OperationCanceledException) { outcome = "Sweep stopped."; }
        catch (Exception error) { outcome = "Sweep ended early: " + error.Message; }
        finally
        {
            run.Measuring = false;
            if (run.Scene is { } scene) { run.Scene = null; try { await scene.DisposeAsync(); } catch (Exception) { } }
            string restore;
            try { await ApplySweepTargetAsync(run.OriginalTarget, run.OriginalAutomatic); restore = $" Restored {run.OriginalTarget:0} Mbps with automatic bitrate {(run.OriginalAutomatic ? "on" : "off")}."; }
            catch (Exception error) { restore = " The original target could not be restored: " + error.Message + " Check Stream settings."; }
            _sweepRunning = false;
            _sweepTask = null;
            SweepStopButton.Visibility = Visibility.Collapsed;
            if (run.QuickCheck && run.Results.Count > 0)
            {
                // The check changes nothing: no ceiling, no preset, no history; the card just says how the rate is doing today.
                _sweepConclusion = LinkCapacitySweep.ConcludeCheck(run.Results[^1]);
                _sweepConcludedAt = DateTimeOffset.Now;
                SweepStatus.Text = _sweepConclusion.Summary;
                RenderSweepResults(run);
            }
            else if (run.Results.Count > 0)
            {
                _sweepConclusion = LinkCapacitySweep.Conclude(run.Results);
                _sweepConcludedAt = DateTimeOffset.Now;
                SweepStatus.Text = _sweepConclusion.Summary + restore;
                RenderSweepResults(run);
                restore += await RecordSweepCeilingAsync(run, _sweepConclusion);
                // The user asked for the result to go live: one sweep, no second click.
                if (run.ApplyWhenDone && outcome == "Sweep finished." && _sweepConclusion.ApplicableCeilingMbps is int ceiling)
                {
                    try { restore += " " + await ApplyMeasuredCeilingAsync(ceiling); }
                    catch (Exception error) { restore += " The measured ceiling could not be applied: " + error.Message; }
                }
                else if (run.ApplyWhenDone && _sweepConclusion.BestCleanMbps is not null)
                    restore += " No new target was applied: the sweep needs measured video near the target and a completed clean confirmation.";
                SweepStatus.Text = _sweepConclusion.Summary + restore;
            }
            else SweepStatus.Text = outcome + restore;
            RefreshCodecButtons();
            ShowStatus(outcome + (run.Results.Count > 0 ? " " + _sweepConclusion!.Summary : "") + (run.QuickCheck ? "" : restore) + run.SceneNote, false);
        }
    }

    /// <summary>Waits until no stream reset request has arrived for <see cref="LinkCapacitySweep.QuietTime"/>, giving up after the limit.</summary>
    private async Task WaitForQuietLinkAsync(CancellationToken token)
    {
        var started = DateTimeOffset.Now;
        var resets = _streamHealth.Current.StreamResetRequests;
        var quietSince = started;
        while (DateTimeOffset.Now - started < LinkCapacitySweep.QuietLimit)
        {
            if (DateTimeOffset.Now - quietSince >= LinkCapacitySweep.QuietTime) return;
            SweepStatus.Text = $"Waiting for the stream to settle ({(DateTimeOffset.Now - quietSince).TotalSeconds:0} s without a recovery request)…";
            await Task.Delay(1000, token);
            var now = _streamHealth.Current.StreamResetRequests;
            if (now != resets) { resets = now; quietSince = DateTimeOffset.Now; }
        }
    }

    /// <summary>
    /// Starts Auto tune's moving test scene for the sweep when asked and it is not already running, so a headset
    /// on the desk gives the encoder real work. A scene that cannot start is noted, not fatal.
    /// </summary>
    private async Task<bool> EnsureSweepSceneAsync(SweepRun run, CancellationToken token)
    {
        if (!run.UseTestScene) return false;
        if (run.Scene is { } running)
        {
            try { _ = running.Current; return false; }
            catch (IOException) { run.Scene = null; try { await running.DisposeAsync(); } catch (Exception) { } }
        }
        var scene = new BenchmarkSceneProcess();
        SweepStatus.Text = "Starting the moving test scene in the headset…";
        try
        {
            await scene.StartAsync(_paths, true, token);
            run.Scene = scene;
            run.SceneNote = "";
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            try { await scene.DisposeAsync(); } catch (Exception) { }
            run.SceneNote = " The moving test scene could not start (" + error.Message.TrimEnd('.') + "), so the sweep measured whatever the headset was showing.";
            return false;
        }
    }

    /// <summary>
    /// Applies the target (unless it is already live), settles, then measures for the step time while the
    /// polls feed <see cref="ObserveSweep"/>. Null means the video link dropped during the step.
    /// </summary>
    private async Task<SweepStepResult?> MeasureStepAsync(SweepRun run, int target, string label, CancellationToken token, bool reapply = true)
    {
        var applied = _streamHealth.Current;
        run.BaseChanges = applied.BitrateChanges; run.LowestDriver = null;
        run.StepStarted = DateTimeOffset.Now;
        run.Measuring = false;
        if (reapply) await ApplySweepTargetAsync(target, false);
        SweepStatus.Text = $"{label}, settling for {LinkCapacitySweep.SettleTime.TotalSeconds:0} s…";
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
        SweepStatus.Text = $"{label}, measuring for {LinkCapacitySweep.MeasureTime.TotalSeconds:0} s…";
        var dropped = false;
        for (var elapsed = TimeSpan.Zero; elapsed < LinkCapacitySweep.MeasureTime; elapsed += TimeSpan.FromSeconds(1))
        {
            await Task.Delay(1000, token);
            if (_streamHealth.Current.LinkActive == false) { dropped = true; break; }
        }
        run.Measuring = false;
        if (dropped) return null;
        var stepHz = run.Fps.Count > 0 ? (int)Math.Round(run.Fps.Average()) : LegacyRefreshHz;
        return LinkCapacitySweep.Evaluate(target, BuildSample(run), stepHz);
    }

    /// <summary>
    /// Keeps the measured ceiling across restarts and saves it as a personal preset built from the
    /// current setup with only the target changed, so one click loads what the link was shown to carry.
    /// </summary>
    private async Task<string> RecordSweepCeilingAsync(SweepRun run, SweepConclusion conclusion)
    {
        // What the PC delivered during the winning step, not a nominal refresh rate: a still scene can run well under 120.
        var hz = run.Fps.Count > 0 ? (int)Math.Round(run.Fps.Average()) : LegacyRefreshHz;
        try
        {
            _preferences.SeedSweepHistory();
            // A sweep with no clean step retires the saved ceiling: the card must not offer a rate the link just refused.
            _preferences.LastSweepCeilingMbps = conclusion.ApplicableCeilingMbps; _preferences.LastSweepAt = DateTimeOffset.Now;
            _preferences.LastSweepSummary = conclusion.Summary; _preferences.LastSweepRefreshHz = hz;
            LinkCapacitySweep.Record(_preferences.SweepHistory, new SweepRecord(DateTimeOffset.Now, conclusion.BestCleanMbps ?? conclusion.HighestMarginalMbps ?? 0, hz, conclusion.Summary, conclusion.BestCleanMbps is null, conclusion.LightlyTested, conclusion.Confirmed));
            await _preferences.SaveAsync();
        }
        catch (Exception error) { return " The measured ceiling could not be saved: " + error.Message; }
        if (conclusion.ApplicableCeilingMbps is not int best) return " No measured-ceiling preset saved; the target was not fully measured and confirmed.";
        try
        {
            var profile = await CaptureEditorProfileAsync();
            var driver = new Dictionary<string, System.Text.Json.JsonElement>(profile.Driver)
            {
                ["targetBandwidth"] = System.Text.Json.JsonSerializer.SerializeToElement((double)best),
                ["automaticBandwidth"] = System.Text.Json.JsonSerializer.SerializeToElement(false)
            };
            var measured = profile with { Driver = driver, TargetMbps = best, SavedAt = DateTimeOffset.Now };
            var name = LinkCapacitySweep.MeasuredPresetName(best, DateTime.Now);
            // Only the latest measurement is worth a preset; earlier app-made ones would just pile up in the list.
            var replaced = await _presetStore.RemoveAsync(preset => LinkCapacitySweep.IsMeasuredPresetName(preset.Name) && preset.Name != name);
            await _presetStore.SaveAsync(name, measured);
            await RefreshPresetsAsync();
            return replaced > 0 ? $" Saved preset “{name}”, replacing the earlier measured-ceiling preset{(replaced == 1 ? "" : "s")}." : $" Saved preset “{name}”.";
        }
        catch (Exception error) { return " The measured-ceiling preset could not be saved: " + error.Message; }
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

    private void RenderSweepResults(SweepRun run, SweepStepResult? pending = null)
    {
        static string Line(SweepStepResult r, string verdict) =>
            $"{r.TargetMbps,4} Mbps  {verdict,-8}  {r.ThrottlesPerMinute,5:0.0} held/min  {(r.Sample.MeanVideoMbps is double v ? $"{v,5:0} Mbps video" : "  video n/a  ")}  {(r.Sample.MaxLossRatio is double l ? $"{l,6:P2} loss" : " loss n/a ")}  {r.Reason}";
        var lines = run.Results.Select(r => Line(r, r.Passes > 1 ? $"{r.Verdict} x{r.Passes}" : r.Verdict.ToString())).ToList();
        if (pending is not null) lines.Add(Line(pending, "Re-check") + " Measuring this target again…");
        SweepResults.Text = string.Join("\n", lines);
    }
}
