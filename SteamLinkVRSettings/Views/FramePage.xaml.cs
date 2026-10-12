using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using SteamLinkVRSettings.Frame;
using SteamLinkVRSettings.Services;
using System.Diagnostics;
using Windows.Foundation;
using Windows.UI;
using Path = System.IO.Path;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage : Page
{
    private readonly SteamPaths _paths = SteamPaths.Current;
    private readonly FramePreferences _preferences = FramePreferences.Load();
    private readonly EncoderProbe _probe = new();
    private readonly BitratePatcher _patcher = new();
    private BitratePatchStatus? _patch;
    private readonly RateTracker _rate = new();
    private readonly QuantizerTracker _quantizers = new();
    private readonly FrameTrafficMonitor _traffic = new();
    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly List<(double At, double? Video, double? Transport, double? Target)> _history = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly FrameConnection _connection;
    private readonly StreamingHealth _streamHealth;
    private readonly StreamingFrameTrace _frameTrace;
    private readonly CompositorTiming _compositorTiming;
    private readonly TemporaryPowerLease _powerLease;
    private bool _syncingPower = true;
    private double _nextPowerRecovery;
    private FrameReceiverDiagnostics _receiver;
    private Task<ConnectionSnapshot>? _linkRead;
    private Task<FrameReceiverSnapshot>? _receiverRead;
    private CancellationTokenSource? _linkReadLifetime;
    private CancellationTokenSource _remoteStop = new();
    private CancellationTokenSource? _linkCancellation;
    private CancellationTokenSource? _receiverCancellation;
    private double _nextReceiverRead;
    private double _nextTraceRead;
    private CancellationTokenSource? _lifetime;
    private bool _disposed;
    private bool _polling;
    private bool _actionPending;
    private bool _initialized;
    private bool _monitorInstalled;
    private bool _syncingBandwidth = true;
    // True while any stream control has unapplied edits; reloads and live reads never overwrite them.
    private bool _bandwidthEdited;
    private bool _policyEdited;
    private long _basicEditRevision;
    private bool _linkEdited;
    private long _linkEditRevision;
    // vrserver presence, sampled off the UI thread once per poll.
    private bool _vrRunning;
    private double? _target;
    private double _nextConnectionRead;
    private double _nextRuntimeRead;
    private double _nextAutoApply;
    private int _configuredPid;
    private ConnectionSnapshot _links = new(null, false, [], "Waiting for headset status…");
    /// <summary>True while a user-started operation (patch, apply, monitor start/stop) is running.</summary>
    public bool IsBusy => _actionPending;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public FramePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        _connection = new(_preferences);
        _streamHealth = StreamingHealth.FromSteamRoot(_paths.SteamRoot);
        _frameTrace = new(Path.Combine(_paths.SteamRoot, "logs"));
        _compositorTiming = new(_paths);
        _powerLease = new(new OpenVrPowerSettings(_paths));
        _receiver = new(_preferences);
        _timer.Tick += async (_, _) => await PollAsync();
        DriverPathInput.Text = _paths.Driver;
        AdbPathInput.Text = _preferences.AdbPath;
        SerialInput.Text = _preferences.AdbSerial;
        SshHostInput.Text = _preferences.SshHost;
        SshKeyInput.Text = _preferences.SshKey;
        SshKnownHostsInput.Text = _preferences.SshKnownHosts;
        GazeInput.Value = Math.Clamp(_preferences.GazeOffset, FoveationOffsets.MinimumGaze, FoveationOffsets.MaximumGaze);
        PeripheralInput.Value = Math.Clamp(_preferences.PeripheralOffset, FoveationOffsets.Minimum, FoveationOffsets.MaximumPeripheral);
        QualityInput.IsOn = _preferences.QualityEnabled;
        ReapplyInput.IsChecked = _preferences.ReapplyAfterRestart;
        RateControlInput.SelectedIndex = Enum.IsDefined(_preferences.RateControl) ? (int)_preferences.RateControl : 0;
        MultiPassInput.SelectedIndex = Enum.IsDefined(_preferences.MultiPass) ? (int)_preferences.MultiPass : 0;
        InitializeAdvancedControls();
        InitializeCodecControls();
        _ = RefreshPresetsAsync();
        _syncingBandwidth = false;
        _syncingPower = false;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        if (_initialized) { await PollAsync(); return; }
        _lifetime?.Dispose();
        _lifetime = new();
        var lifetime = _lifetime;
        await LoadControlsAsync();
        await LoadSteamVrVideoAsync();
        if (_disposed || lifetime.IsCancellationRequested || !ReferenceEquals(lifetime, _lifetime)) return;
        if (!_initialized)
        {
            _initialized = true;
            if (_powerLease.RecoveryPending && !CodecHooksPaused(await _codecController.ReadStatusAsync()))
                await ActionAsync(async () => { try { await _powerLease.RestoreAsync(); return "Recovered the app's previous temporary power settings."; } finally { UpdatePowerLease(); } }, reportSuccess: false);
            await ActionAsync(InspectPatchAsync, reportSuccess: false);
            if (_disposed || lifetime.IsCancellationRequested || !ReferenceEquals(lifetime, _lifetime)) return;
        }
        _timer.Start();
        await PollAsync();
    }
    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        // The cached stream page keeps recording while Additional settings is open.
        // CloseAsync owns timer, request and connection cleanup for the whole app.
    }
    public async Task CloseAsync()
    {
        // Release only the ephemeral port created by this page, never the ADB server.
        await _actions.WaitAsync();
        try
        {
            try
            {
                if (!CodecHooksPaused(await _codecController.ReadStatusAsync())) await _powerLease.RestoreAsync();
                else if (_powerLease.RecoveryPending) PowerLeaseStatus.Text = "Power restoration pending until the codec reconnect finishes; original values remain saved.";
            }
            catch (Exception error) when (_powerLease.RecoveryPending)
            {
                // A running vrserver may still reject OpenVR initialization when
                // the Frame disconnects. Closing is best-effort; the already
                // durable app lease is retried on the next available session.
                PowerLeaseStatus.Text = "Power restoration pending: " + error.Message + " Original values remain saved for recovery when the app and SteamVR are available.";
            }
            _disposed = true;
            _timer.Stop();
            _lifetime?.Cancel();
            if (_linkRead is not null) { try { await _linkRead; } catch (OperationCanceledException) { } }
            if (_receiverRead is not null) { try { await _receiverRead; } catch (OperationCanceledException) { } }
            await _connection.DisposeAsync();
        }
        finally { _actions.Release(); }
    }
    private void UpdatePowerLease()
    {
        _syncingPower = true;
        try { KeepAwakeInput.IsOn = _powerLease.Enabled; }
        finally { _syncingPower = false; }
        PowerLeaseStatus.Text = _powerLease.Enabled ? "Temporary awake test active; original power values saved."
            : _powerLease.RecoveryPending ? "Power restoration pending; start SteamVR, then disable this control or retry closing."
            : "No temporary power lease";
    }
    private async void KeepAwake_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingPower) return;
        var enabled = KeepAwakeInput.IsOn;
        await ActionAsync(async () =>
        {
            await EnsureCodecStableAsync();
            if (enabled) await _powerLease.EnableAsync(); else await _powerLease.RestoreAsync();
            return enabled ? "Temporary unattended-test power settings enabled." : "Original SteamVR power settings restored; newer differing values preserved.";
        });
        UpdatePowerLease();
    }
    private async Task LoadControlsAsync()
    {
        // Returning to this cached page must not discard edits that were not applied yet.
        if (_bandwidthEdited) return;
        _syncingBandwidth = true;
        try
        {
            var values = await new SettingsService().LoadMergedAsync();
            var pictureValues = SelectedCodec == StreamCodec.Hevc ? values : await new SettingsService().LoadDefaultsAsync();
            if (SelectedCodec == StreamCodec.Hevc && values.TryGetValue("targetBandwidth", out var target)) BitrateInput.Value = Convert.ToDouble(target);
            if (values.TryGetValue("automaticBandwidth", out var automatic)) AutomaticInput.IsOn = automatic is true;
            if (values.TryGetValue("allowMultipleLinks", out var multiple)) MultipleLinksInput.IsOn = multiple is true;
            if (values.TryGetValue("enableAmfEncoder", out var amf)) AmfInput.IsOn = amf is true;
            if (pictureValues.TryGetValue("streamFormatWidth", out var width)) StreamWidthInput.Value = Convert.ToDouble(width);
            if (pictureValues.TryGetValue("automaticStreamFormatWidth", out var automaticWidth)) AutomaticWidthInput.IsOn = automaticWidth is true;
            _target = double.IsFinite(BitrateInput.Value) ? BitrateInput.Value : null;
            TargetReadback.Text = _target is double t ? $"{t:0} Mbps" : "— Mbps";
            TargetMode.Text = "Saved setting";
        }
        catch (Exception ex) { ShowStatus("Could not load stream controls: " + ex.Message, true); }
        finally { _syncingBandwidth = false; _bandwidthEdited = false; }
    }
    private void Bandwidth_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_syncingBandwidth) { _bandwidthEdited = true; _basicEditRevision++; UpdateStreamEditStatus(); }
    }
    private void Bandwidth_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_syncingBandwidth)
        {
            _bandwidthEdited = true; _basicEditRevision++;
            if (ReferenceEquals(sender, MultipleLinksInput)) { _linkEdited = true; _linkEditRevision++; }
            UpdateStreamEditStatus();
        }
    }
    private void Policy_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingBandwidth) { _policyEdited = true; }
    }
    private bool ContainsFocus(DependencyObject control)
    {
        if (XamlRoot is null) return false;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, control)) return true;
            focused = VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    private async Task PollAsync()
    {
        if (_polling || _actionPending || _lifetime is null || _lifetime.IsCancellationRequested || !await _actions.WaitAsync(0)) return;
        var lifetime = _lifetime;
        _polling = true;
        try
        {
            var at = Now;
            var runningTask = Task.Run(() => OpenVrSettings.IsRunning);
            // A paired switch installs its backend into the fresh process before ordinary hooks may start.
            var codecService = await _codecController.ReadStatusAsync(lifetime.Token);
            if (lifetime.IsCancellationRequested || !ReferenceEquals(lifetime, _lifetime)) return;
            RenderCodecService(codecService);
            var codecPaused = CodecHooksPaused(codecService);
            var nativeTask = codecPaused ? Task.FromResult(new EncoderSnapshot { Message = "Codec session is reconnecting; encoder monitoring is paused." }) : _probe.GetSnapshotAsync();
            var trafficTask = Task.Run(() => _traffic.Sample());
            var healthTask = _streamHealth.PollAsync(lifetime.Token);
            var compositorTask = codecPaused
                ? Task.FromResult(_compositorTiming.Current with { Available = false, ObservedAt = DateTimeOffset.Now,
                    Message = "Codec session is reconnecting; PC compositor timing is paused." })
                : _compositorTiming.PollAsync(lifetime.Token);
            var traceTask = at >= _nextTraceRead ? Task.Run(() => _frameTrace.ReadLatest(), lifetime.Token) : null;
            if (traceTask != null) _nextTraceRead = at + 5;
            // Remote monitoring must not stall the one-second video graph if Wi-Fi disappears.
            if (_linkRead is null && at >= _nextConnectionRead)
            {
                _linkReadLifetime = lifetime;
                _linkCancellation?.Dispose();
                _linkCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, _remoteStop.Token);
                _linkRead = _connection.ReadAsync(_linkCancellation.Token);
                _nextConnectionRead = at + 5;
            }
            if (_receiverRead is null && at >= _nextReceiverRead)
            {
                _receiverCancellation?.Dispose();
                _receiverCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, _remoteStop.Token);
                _receiverRead = _receiver.PollAsync(_receiverCancellation.Token);
                _nextReceiverRead = at + 2;
            }
            await Task.WhenAll(runningTask, nativeTask, trafficTask, healthTask, compositorTask, (Task?)traceTask ?? Task.CompletedTask);
            _vrRunning = await runningTask;
            if (!codecPaused && _vrRunning && _powerLease.RecoveryPending && !_powerLease.Enabled && at >= _nextPowerRecovery)
            {
                _nextPowerRecovery = at + 30;
                try { await _powerLease.RestoreAsync(); }
                catch (Exception error) { ShowStatus("Temporary power recovery is pending: " + error.Message, true); }
                UpdatePowerLease();
            }
            var snapshot = await nativeTask;
            _monitorInstalled = snapshot.Installed;
            var adapters = await trafficTask;
            if (_linkRead is { IsCompleted: true })
            {
                if (_linkRead.IsCompletedSuccessfully && _linkReadLifetime == lifetime) _links = await _linkRead;
                else _ = _linkRead.Exception;
                _linkRead = null;
            }
            if (_receiverRead is { IsCompleted: true })
            {
                _ = _receiverRead.Exception;
                _receiverRead = null;
            }
            if (lifetime.IsCancellationRequested || !ReferenceEquals(lifetime, _lifetime)) return;

            var health = await healthTask;
            HealthSummary.Text = health.Summary + (health.CaptureError is null ? "" : " Capture file unavailable: " + health.CaptureError);
            HealthCounts.Text = $"This app session: {health.StreamResetRequests} recovery requests • {health.NetworkBadLinkEvents} network delays • {health.EncoderPerformanceWarnings} encoder stalls • {health.CompositorAcquireTimeouts} compositor handoff warnings";
            var compositor = await compositorTask;
            CompositorStatus.Text = compositor.Available && compositor.ScenePid is int scene
                ? $"PC scene {scene}: {compositor.DroppedFrames} dropped presentations • {compositor.ReprojectedFrames} reprojected • {compositor.TimedOutEvents} timeout events • {compositor.LateRenderFrames} frames on an unpredicted vsync"
                : compositor.Message;
            CompositorDetail.Text = compositor.Available && compositor.ScenePid is not null
                ? $"Current sample GPU p99 {Timing(compositor.TotalGpu)} / frame budget {(compositor.TargetFrameMs is double budget ? $"{budget:0.0} ms" : "unavailable")} • {compositor.SampledFrames} sampled frames • {compositor.FrameHistoryGaps} unobserved history entries. Counters start fresh for each SteamVR/scene session."
                : "PC presentation counters use SteamVR's public compositor API.";
            if (compositor.CaptureError is not null) CompositorDetail.Text += " " + compositor.CaptureError;
            var receiver = _receiver.Current;
            ReceiverStatus.Text = receiver.Available
                ? $"Frame receiver connected over wireless • {(receiver.StreamPid is int pid ? $"stream PID {pid}" : "no stream process detected")} • updated {receiver.ObservedAt.ToLocalTime():HH:mm:ss}"
                : receiver.Message;
            ReceiverCounts.Text = $"Receiver this app session: {receiver.RetryEvents} retry requests ({receiver.RequestedRetryPackets} requested packets) • {receiver.BacktrackWindowResets} backtrack resets • {receiver.UnrecoverableErrors} unrecoverable errors • {receiver.LateMetadataEvents} late metadata • {receiver.EmptyDecodeBuffers} missing buffers • {receiver.DecodeBufferMismatches} buffer mismatches • {receiver.DecoderQueueFailures} decoder queue failures";
            if (receiver.CaptureError is not null) ReceiverStatus.Text += " " + receiver.CaptureError;
            if (receiver.SkippedBytes + receiver.DiscardedLines + receiver.CaptureSkippedEvents > 0)
                ReceiverStatus.Text += $" Capture gaps: {receiver.SkippedBytes} skipped bytes, {receiver.DiscardedLines} discarded lines, {receiver.CaptureSkippedEvents} omitted event records.";
            var events = health.RecentEvents.Select(e => (e.Timestamp, Message: "PC: " + e.Message))
                .Concat(compositor.RecentEvents.Select(e => (e.Timestamp, Message: $"PC presentation (scene {e.ScenePid}): {e.DroppedFrames} drops, {e.ReprojectedFrames} reprojected, {e.TimedOutEvents} timeouts, {e.LateRenderFrames} unpredicted-vsync frames")))
                .Concat(receiver.RecentEvents.Select(e => (e.Timestamp, Message: "Frame: " + e.Message)))
                .OrderByDescending(e => e.Timestamp).Take(12).ToArray();
            HealthEvents.Text = events.Length == 0 ? "No new streaming warnings captured." : string.Join("\n", events.Select(e => $"{e.Timestamp.ToLocalTime():HH:mm:ss.fff}  {e.Message}"));
            if (traceTask != null && await traceTask is { } trace)
                HealthTrace.Text = $"Last recovery trace {trace.LastFrame?.ToLocalTime():HH:mm:ss} ({TraceAge(trace.LastFrame)}): {trace.SentFrames} sent frames. " +
                    $"Worst encode {Stage(trace.Encode)}, send-to-receive {Stage(trace.Transport)}, decode {Stage(trace.Decode)}. " +
                    $"{trace.LateSubmissionFrames} headset submissions >1 ms late; {trace.MissingIlluminationFeedback} without submission feedback.\n{trace.Message}";

            RuntimeText.Text = _vrRunning ? "SteamVR running" : "SteamVR stopped";
            UpdatePatchButtons();
            if (codecPaused)
            {
                _target = null;
                TargetReadback.Text = "— Mbps";
                TargetMode.Text = "Codec session reconnecting; live settings read paused";
                SupersampleFilteringReadback.Text = "Codec session reconnecting; SteamVR video readback paused.";
            }
            else if (_vrRunning && at >= _nextRuntimeRead)
            {
                _nextRuntimeRead = at + 3;
                try
                {
                    var live = await Task.Run(() => OpenVrSettings.Read(_paths));
                    var supersample = await Task.Run(() => OpenVrSettings.ReadSupersampleFiltering(_paths));
                    if (lifetime.IsCancellationRequested || !ReferenceEquals(lifetime, _lifetime)) return;
                    _target = live.Target;
                    if (!_bandwidthEdited && _pendingPreset == null) ShowSupersampleValue(supersample, "Live SteamVR readback");
                    TargetReadback.Text = $"{live.Target:0} Mbps";
                    TargetMode.Text = live.Automatic ? "Live • automatic" : "Live • manual";
                    // External/live changes should become the next Apply value,
                    // while an in-progress user edit remains untouched.
                    if (!_bandwidthEdited && (SelectedCodec == StreamCodec.Hevc && !snapshot.PairedCodecTrialPresent || _codecService.Live && _codecService.ActiveCodec == (SelectedCodec == StreamCodec.Av1 ? "AV1" : "H.264")) && !ContainsFocus(BitrateInput) && !ContainsFocus(AutomaticInput) && !ContainsFocus(StreamWidthInput) && !ContainsFocus(AutomaticWidthInput))
                    {
                        _syncingBandwidth = true;
                        try
                        {
                            BitrateInput.Value = live.Target; AutomaticInput.IsOn = live.Automatic;
                            if (SelectedCodec == StreamCodec.Hevc) { StreamWidthInput.Value = live.StreamWidth; AutomaticWidthInput.IsOn = live.AutomaticWidth; }
                        }
                        finally { _syncingBandwidth = false; }
                    }
                    if (!_linkEdited && !ContainsFocus(MultipleLinksInput))
                    {
                        _syncingBandwidth = true;
                        try { MultipleLinksInput.IsOn = live.MultipleLinks; }
                        finally { _syncingBandwidth = false; }
                    }
                }
                catch (Exception ex) { TargetMode.Text = "Live read unavailable: " + ex.Message; _target = null; TargetReadback.Text = "— Mbps"; }
            }
            else if (!_vrRunning)
            {
                _target = null;
                TargetReadback.Text = "— Mbps";
                TargetMode.Text = "Start SteamVR to read the live target";
            }
            if (_preferences.ReapplyAfterRestart && !codecPaused && !snapshot.PairedCodecTrialPresent && snapshot.ProcessId > 0 && _configuredPid != snapshot.ProcessId && at >= _nextAutoApply && !CodecHooksPaused(await _codecController.ReadStatusAsync(lifetime.Token)))
            {
                _nextAutoApply = at + 10;
                snapshot = await _probe.StartAsync();
                if (snapshot.Installed)
                {
                    snapshot = await _probe.SetQualityAsync(_preferences.QualityEnabled, _preferences.GazeOffset, _preferences.PeripheralOffset);
                    var policy = new EncodingPolicy(_preferences.RateControl, _preferences.MultiPass);
                    // Arm every native request before an OpenVR refresh. A fresh
                    // server may have no connected Frame yet (OpenVR error216),
                    // but the public NVENC hooks can already catch initialization.
                    if (policy.IsValid) snapshot = await _probe.SetEncodingPolicyAsync(policy);
                    var advanced = _preferences.AdvancedEncoding ?? new();
                    if (snapshot.AdvancedEncoding is { Supported: true })
                        snapshot = await _probe.SetAdvancedEncodingAsync(advanced);
                    _configuredPid = snapshot.ProcessId;
                    if (snapshot.Active)
                    {
                        try
                        {
                            await Task.Run(() => OpenVrSettings.RefreshEncoder(_paths));
                            snapshot = await _probe.GetSnapshotAsync();
                        }
                        catch (Exception error) { ShowStatus("Native requests are armed; live refresh unavailable: " + error.Message + " Connect the Frame, or restart SteamVR if initialization-only changes stay pending.", false); }
                    }
                }
                if (lifetime.IsCancellationRequested || !ReferenceEquals(lifetime, _lifetime)) return;
            }
            var measured = snapshot.Installed && snapshot.Active
                ? _rate.Sample(snapshot.Generation, snapshot.EncodedBytes, snapshot.EncodedFrames, snapshot.TimestampSeconds)
                : (Mbps: (double?)null, Fps: (double?)null);
            if (!snapshot.Installed || !snapshot.Active) { _rate.Reset(); _quantizers.Reset(); }
            VideoRate.Text = measured.Mbps is double mbps ? mbps.ToString("0.0") : "—";
            VideoDetail.Text = measured.Fps is double fps ? $"{fps:0.0} frames/s" : snapshot.Installed ? "Waiting for encoded frames…" : "Start the live monitor during an NVENC stream.";
            var codec = StreamCodecStatus.From(snapshot);
            CodecReadback.Text = "Codec: " + codec.Value;
            CodecDetail.Text = codec.Detail;
            CodecCurrentReadback.Text = "Current encoder: " + codec.Value + " • " + codec.Detail;
            if (codecPaused) MarkCodecReconnect();
            CodecReadback.Foreground = new SolidColorBrush(codec.Verified ? Color.FromArgb(255, 92, 189, 118) : Color.FromArgb(255, 200, 200, 208));
            ProbeStatus.Text = snapshot.Message + (snapshot.Installed ? $"  Encode errors: {snapshot.EncodeErrors}; output errors: {snapshot.LockErrors}; frames measured: {snapshot.EncodedFrames}." : "");
            QualityReadback.Text = snapshot.PairedCodecTrialPresent ? "Foveation override: disabled for the paired codec trial." : snapshot.Enabled ? $"Custom foveation: gaze {snapshot.CenterOffset:+0;-0;0}, periphery {snapshot.PeripheralOffset:+0;-0;0} • {snapshot.ModifiedFrames} frames changed" : snapshot.Installed ? snapshot.QualitySupported ? "Foveation: Valve defaults" : "Video monitoring available; this driver's foveation layout is incompatible." : "Foveation status available when the monitor is running";
            QualityInput.IsEnabled = GazeInput.IsEnabled = PeripheralInput.IsEnabled =
                RateControlInput.IsEnabled = MultiPassInput.IsEnabled = !snapshot.PairedCodecTrialPresent;
            if (!_policyEdited && !snapshot.PairedCodecTrialPresent && snapshot.Installed && !ContainsFocus(RateControlInput) && !ContainsFocus(MultiPassInput) &&
                !ContainsFocus(QualityInput) && !ContainsFocus(GazeInput) && !ContainsFocus(PeripheralInput))
            {
                _syncingBandwidth = true;
                try
                {
                    if (snapshot.EncodingPolicy is { } currentPolicy)
                    {
                        RateControlInput.SelectedIndex = (int)currentPolicy.Requested.RateControl;
                        MultiPassInput.SelectedIndex = (int)currentPolicy.Requested.MultiPass;
                    }
                    QualityInput.IsOn = snapshot.Enabled;
                    if (snapshot.Enabled) { GazeInput.Value = snapshot.CenterOffset; PeripheralInput.Value = snapshot.PeripheralOffset; }
                }
                finally { _syncingBandwidth = false; }
            }
            RenderEncodingReadbacks(snapshot);
            RenderAdvancedReadbacks(snapshot);
            try
            {
                if (!codecPaused) await RefreshAdvancedDriverAsync();
                else foreach (var row in _driverRows.Values) row.Readback!.Text = "Codec reconnecting; OpenVR readback paused.";
            }
            catch (Exception error) { foreach (var row in _driverRows.Values) row.Readback!.Text = "Advanced settings read unavailable: " + error.Message; }
            RefreshCodecButtons();
            WiredReadback.Text = _links.VideoPath;
            WiredReadback.Foreground = new SolidColorBrush(_links.ActiveLinks.Length > 0 ? Color.FromArgb(255, 92, 189, 118) : Color.FromArgb(255, 200, 200, 208));
            WifiReadback.Text = _links.WifiEnabled switch { true => "Wi-Fi enabled", false => "Wi-Fi disabled — enable it to connect", _ => "Wi-Fi: unknown" };
            LinkReadback.Text = _links.HasInfo ? string.Join("\n", _links.Links.Where(l => l.Type != 255 && l.Type != 4).Select(l => $"{l.Name}: {(l.Active ? "active" : "inactive")} • {l.LatencyMs:0.0} ms • {l.LossRatio:P2} loss")) : "Active links unavailable";
            LinkStatus.Text = _links.Message;
            UsbTraffic.Text = adapters.Count == 0 ? "No Steam Frame dongle or USB network adapter detected." : string.Join("\n", adapters.Select(a => $"{(a.LinkType == 2 ? "Dedicated dongle" : "USB cable")} — {a.Name}: {(a.Up ? "up" : "down")} • {a.Address}\nSend {Rate(a.SendMbps)} Mbps / receive {Rate(a.ReceiveMbps)} Mbps"));
            _history.Add((Now, measured.Mbps, FrameTrafficMonitor.ActiveSendMbps(_links, adapters), _target));
            _history.RemoveAll(x => x.At < Now - 60);
            DrawGraph();
        }
        catch (Exception ex)
        {
            if (lifetime.IsCancellationRequested || !ReferenceEquals(lifetime, _lifetime)) return;
            VideoRate.Text = "—";
            CodecReadback.Text = "Codec: Unknown";
            CodecDetail.Text = "Current encoder evidence is unavailable.";
            CodecCurrentReadback.Text = "Current encoder: Unknown • current evidence unavailable";
            CodecReadback.Foreground = new SolidColorBrush(Color.FromArgb(255, 200, 200, 208));
            ProbeStatus.Text = "Monitor unavailable: " + ex.Message;
            _rate.Reset();
        }
        finally { _polling = false; _actions.Release(); }
    }
    private static string Rate(double? value) => value is double d ? d.ToString("0.0") : "—";
    private void RenderEncodingReadbacks(EncoderSnapshot snapshot)
    {
        var policy = snapshot.EncodingPolicy;
        PolicyRequestedReadback.Text = snapshot.PairedCodecTrialPresent ? "The paired codec trial controls the encoder policy; app overrides are disabled."
            : policy is null ? "Requested NVENC policy: unavailable (updated helper required)" : "Requested NVENC policy: " + policy.Requested.Description;
        PolicyAppliedReadback.Text = policy is not { Observed: true } ? "Accepted NVENC configuration: waiting for a verified initialize/reconfigure call" :
            $"Last accepted: {policy.RateControlName} / {policy.MultiPassName} / preset {policy.Preset} • {policy.AverageBitrate / 1_000_000.0:0.0} Mbps encoder target\n" +
            $"{policy.Width} × {policy.Height} at {(policy.FrameRateDen > 0 ? policy.FrameRateNum / (double)policy.FrameRateDen : 0):0.0} fps • bit depth {(snapshot.BitDepth is uint depth ? depth.ToString() : "unavailable")} • VBV {policy.VbvSize / 1_000_000.0:0.00} Mbit / initial {policy.VbvDelay / 1_000_000.0:0.00} Mbit • padding {(policy.Filler ? "enabled" : "disabled")}\n" +
            (snapshot.PairedCodecTrialPresent ? "Paired codec adapter configuration observed." : policy.Requested == policy.Applied ? "Requested policy accepted." : "Policy change pending; reconnect if it does not apply.") +
            $" Calls {policy.Calls}, errors {policy.Errors}, rejected ABI calls {policy.RejectedCalls}." + (snapshot.Active ? "" : " Stream currently inactive.");
        var qp = snapshot.Active ? _quantizers.Sample(snapshot.Generation, snapshot.QuantizerSamples, snapshot.QuantizerSum) : null;
        QuantizerReadback.Text = $"Whole-frame QP: {(qp is double value ? value.ToString("0.00") + " interval mean" : "waiting for new samples")} • last frame {(snapshot.QuantizerSamples > 0 ? snapshot.LastFrameQp?.ToString() : "unavailable")} • {snapshot.LastFrameBytes / 1024.0:0.0} KiB/frame";
        CenterMapReadback.Text = snapshot.LastQualityMap is not { TimestampSeconds: > 0 } map ? "Gaze map deltas: unavailable" :
            $"Last gaze map: {map.CenterChangedTiles}/{map.CenterTiles} center tiles changed, deltas {map.CenterMinDelta:+0;-0;0} to {map.CenterMaxDelta:+0;-0;0}, {map.CenterClampedTiles} clamped. " +
            $"Outside gaze: {map.PeripheralChangedTiles}/{map.PeripheralTiles} changed, {map.PeripheralSaturatedTiles} saturated; protected mismatches {map.ProtectedMismatches}.";
    }

    private async Task<EncoderSnapshot> ApplyPolicyAsync(EncodingPolicy policy)
    {
        await EnsureCodecStableAsync();
        var result = await _probe.SetEncodingPolicyAsync(policy);
        if (!result.Installed || result.EncodingPolicy?.Requested != policy) return result;
        await Task.Run(() => OpenVrSettings.RefreshEncoder(_paths));
        return await _probe.GetSnapshotAsync();
    }
    private static string Stage(StageTiming? value) => value is null ? "unavailable" : $"{value.MaximumMs:0.0} ms";
    private static string Timing(CompositorTimingStats? value) => value is null ? "unavailable" : $"{value.P99Ms:0.0} ms";
    private static string TraceAge(DateTimeOffset? timestamp)
    {
        if (timestamp is null) return "age unavailable";
        var age = DateTimeOffset.Now - timestamp.Value;
        return age.TotalMinutes >= 1 ? $"{Math.Max(0, age.TotalMinutes):0} min ago" : $"{Math.Max(0, age.TotalSeconds):0} sec ago";
    }
    private void OpenCaptureFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.GetDirectoryName(_streamHealth.CapturePath)!;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowStatus("Could not open capture folder: " + ex.Message, true); }
    }

    private async void StartMonitor_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        await EnsureCodecStableAsync();
        var status = await _probe.StartAsync();
        _monitorInstalled = status.Installed;
        if (!status.Installed)
        {
            // A running, compatible SteamVR that still refuses the monitor may need more rights.
            if (!Elevation.IsElevated && status.ProcessId > 0 && status.Supported)
                throw new ElevationSuggestedException(status.Message + " If this keeps happening, restart this app as administrator.");
            throw new InvalidOperationException(status.Message);
        }
        _rate.Reset();
        _configuredPid = status.ProcessId;
        return "Live monitor started. Encoded bitrate appears after two samples.";
    });
    private async void StopMonitor_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        await EnsureCodecStableAsync();
        // Prevent automatic reapplication while restoration is pending.
        _preferences.ReapplyAfterRestart = false;
        ReapplyInput.IsChecked = false;
        await _preferences.SaveAsync();
        var before = await _probe.GetSnapshotAsync();
        if (before.Installed && before.AdvancedEncoding is not null &&
            (!before.AdvancedEncoding.Requested.IsDefault || before.AdvancedEncoding.AppliedFlags != 0 || before.AdvancedEncoding.PendingFlags != 0))
        {
            await _probe.SetAdvancedEncodingAsync(new());
            await Task.Run(() => OpenVrSettings.RefreshEncoder(_paths));
            before = await _probe.GetSnapshotAsync();
        }
        if (before.Installed && before.EncodingPolicy is not null &&
            (!before.EncodingPolicy.Requested.IsDefault || before.EncodingPolicy.Observed && !before.EncodingPolicy.Applied.IsDefault))
            await ApplyPolicyAsync(new());
        var status = await _probe.StopAsync();
        _monitorInstalled = status.Installed;
        if (status.Installed) throw new IOException(status.Message);
        _preferences.ReapplyAfterRestart = false;
        ReapplyInput.IsChecked = false;
        await _preferences.SaveAsync();
        _rate.Reset();
        _quantizers.Reset();
        return before.PairedCodecTrialPresent ? "Paired codec monitoring stopped. The paired codec trial remains active."
            : "Live monitor stopped; Valve's original foveation and encoder policy restored for this session.";
    });
    private async void Apply_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        await EnsureCodecStableAsync();
        if (!double.IsFinite(BitrateInput.Value) || BitrateInput.Value % 1 != 0) throw new ArgumentException("Use a whole-number bitrate target.");
        var suppliedPassword = CodecPasswordInput.Password;
        CodecPasswordInput.Password = "";
        var presetNeedsReconnect = _pendingPreset != null;
        await PreparePendingPresetAsync();
        if (SelectedCodec != StreamCodec.Hevc)
        {
            var current = await _codecController.ReadStatusAsync();
            var codecName = SelectedCodec == StreamCodec.Av1 ? "AV1" : "H.264";
            var fps = CodecRefreshInput.SelectedIndex == 1 ? 120 : 90;
            var message = !presetNeedsReconnect && current.Live && current.State == "ready" && current.ActiveCodec == codecName && current.TargetMbps == BitrateInput.Value && current.Fps == fps
                ? "The selected codec setup is already running." : await ApplySelectedCodecAsync(suppliedPassword);
            suppliedPassword = null;
            await ApplyPendingPresetAsync(paired: true);
            await ApplySupersampleAsync();
            _presetRestartNeeded = false;
            _bandwidthEdited = _codecEdited = false; UpdateStreamEditStatus(); return message;
        }
        if (_codecService.Live) { await EndCodecSessionAsync(); await WaitForHevcSettingsAsync(); }
        if (!double.IsFinite(BitrateInput.Value) || BitrateInput.Value < 25 || BitrateInput.Value > 500) throw new ArgumentException("Choose a bitrate between 25 and 500 Mbps.");
        if (!FoveationOffsets.IsValid(GazeInput.Value, FoveationOffsets.MaximumGaze, FoveationOffsets.MinimumGaze) || !FoveationOffsets.IsValid(PeripheralInput.Value, FoveationOffsets.MaximumPeripheral))
            throw new ArgumentException("Use whole-number offsets: gaze −20 to +12, outside gaze −12 to +31.");
        if (!double.IsFinite(StreamWidthInput.Value) || StreamWidthInput.Value < 512 || StreamWidthInput.Value > 4096 || StreamWidthInput.Value % 32 != 0)
            throw new ArgumentException("Choose a stream width from 512 to 4096 pixels, in multiples of 32.");
        // Capture the request before asynchronous work. Later edits remain a
        // separate pending request instead of changing what this Apply saves.
        var enteredTarget = BitrateInput.Value;
        var requestRevision = _basicEditRevision;
        var requestLinkRevision = _linkEditRevision;
        var requestedLinkWasEdited = _linkEdited;
        var requestedTarget = Math.Round(enteredTarget);
        var requestedAutomatic = AutomaticInput.IsOn;
        var requestedMultiple = MultipleLinksInput.IsOn;
        var requestedAmf = AmfInput.IsOn;
        var requestedQuality = QualityInput.IsOn;
        var requestedGaze = (int)GazeInput.Value;
        var requestedPeriphery = (int)PeripheralInput.Value;
        var requestedReapply = ReapplyInput.IsChecked == true;
        var requestedWidth = (int)StreamWidthInput.Value;
        var requestedAutomaticWidth = AutomaticWidthInput.IsOn;
        var requestedPolicy = new EncodingPolicy(_pendingPreset != null ? (EncoderRateControl)RateControlInput.SelectedIndex : _preferences.RateControl, _pendingPreset != null ? (EncoderMultiPass)MultiPassInput.SelectedIndex : _preferences.MultiPass);
        _ = requestedPolicy.Packed;
        // Check the driver selected in Patches & Recovery, not only the auto-discovered path.
        var driverPath = DriverPathInput.Text.Trim().Trim('"');
        var driver = await _patcher.InspectAsync(string.IsNullOrWhiteSpace(driverPath) ? _paths.Driver : driverPath);
        if (!requestedAutomatic && driver.State == BitratePatchState.Original && requestedTarget > 232)
            throw new InvalidOperationException("This original driver has a 232 Mbps ceiling when wireless is present. Install the bitrate override in Patches & Recovery before requesting a higher manual target.");
        var values = new Dictionary<string, object?>
        {
            ["automaticBandwidth"] = requestedAutomatic,
            // Stored as a float, matching SteamVR and the Additional settings editor.
            // recommendedBandwidthMbit is edited under Additional settings.
            ["targetBandwidth"] = requestedTarget,
            ["allowMultipleLinks"] = requestedMultiple,
            ["enableAmfEncoder"] = requestedAmf,
            ["streamFormatWidth"] = requestedWidth,
            ["automaticStreamFormatWidth"] = requestedAutomaticWidth
        };
        bool running = await Task.Run(() => OpenVrSettings.IsRunning);
        if (running)
        {
            // Unedited link state can change outside this app between polls.
            // Preserve the latest runtime value instead of reapplying a stale toggle.
            if (!requestedLinkWasEdited) values["allowMultipleLinks"] = (await Task.Run(() => OpenVrSettings.Read(_paths))).MultipleLinks;
            await Task.Run(() => OpenVrSettings.Apply(_paths, values));
        }
        else await new SettingsService().SaveUserOverridesAsync(values);
        var before = await _probe.GetSnapshotAsync();
        string qualityMessage = "";
        if (before.PairedCodecTrialPresent) qualityMessage = " App encoder and foveation overrides remain disabled during the paired codec trial.";
        else if (running && (requestedQuality || !requestedPolicy.IsDefault || before.Installed))
        {
            if (!before.Installed) before = await _probe.StartAsync();
            if (!before.Installed) qualityMessage = " Foveation pending: " + before.Message;
            else
            {
                var after = await _probe.SetQualityAsync(requestedQuality, requestedGaze, requestedPeriphery);
                if (after.Enabled != requestedQuality) qualityMessage = " Foveation was not applied: " + after.Message;
                else _configuredPid = after.ProcessId;
                if (before.EncodingPolicy?.Requested != requestedPolicy || before.EncodingPolicy.Observed == false)
                {
                    var policyResult = await ApplyPolicyAsync(requestedPolicy);
                    qualityMessage += policyResult.EncodingPolicy is { Observed: true } accepted && accepted.Requested == requestedPolicy && accepted.Applied == requestedPolicy
                        ? " NVENC policy accepted."
                        : " NVENC policy pending: " + policyResult.Message + " Reconnect if the dashboard does not confirm it.";
                }
            }
        }
        _preferences.GazeOffset = requestedGaze;
        _preferences.PeripheralOffset = requestedPeriphery;
        _preferences.QualityEnabled = requestedQuality;
        _preferences.ReapplyAfterRestart = requestedReapply;
        _preferences.RateControl = requestedPolicy.RateControl;
        _preferences.MultiPass = requestedPolicy.MultiPass;
        _preferences.RequestedCodec = StreamCodec.Hevc;
        await _preferences.SaveAsync();
        if (_basicEditRevision == requestRevision) _bandwidthEdited = false;
        if (_linkEditRevision == requestLinkRevision) _linkEdited = false;
        await ApplyPendingPresetAsync(paired: false);
        await ApplySupersampleAsync();
        if (_presetRestartNeeded) { SteamVrRuntime.Start(_paths); _presetRestartNeeded = false; }
        _codecEdited = false; UpdateStreamEditStatus();
        return (running ? "Stream settings accepted by SteamVR. Encoder selection and width changes may require reconnecting." : "Stream settings saved for the next session. Start SteamVR and Apply to set foveation and NVENC policy.") + qualityMessage;
    });
    private void QualityPreset_Click(object sender, RoutedEventArgs e) { QualityInput.IsOn = true; GazeInput.Value = -4; PeripheralInput.Value = 8; }
    private void QualityDefault_Click(object sender, RoutedEventArgs e) { QualityInput.IsOn = false; GazeInput.Value = 0; PeripheralInput.Value = 0; }
    private async void SaveConnection_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        // Drain reads of the previous endpoint before changing shared connection settings.
        _remoteStop.Cancel();
        if (_linkRead is not null) { try { await _linkRead; } catch (OperationCanceledException) { } }
        if (_receiverRead is not null) { try { await _receiverRead; } catch (OperationCanceledException) { } }
        _linkRead = null; _receiverRead = null;
        _linkCancellation?.Dispose(); _linkCancellation = null;
        _receiverCancellation?.Dispose(); _receiverCancellation = null;
        _remoteStop.Dispose(); _remoteStop = new();
        ReadConnectionInputs();
        _receiver = new(_preferences);
        _links = new(null, false, [], "Refreshing the selected headset…");
        await _preferences.SaveAsync();
        _nextConnectionRead = 0;
        _nextReceiverRead = 0;
        var result = await _connection.ReadAsync();
        _links = result; ConnectionTestStatus.Text = result.Message;
        return result.HasInfo ? "Connection saved and headset status received." : "Connection saved. " + result.Message;
    });
    private void ReadConnectionInputs()
    {
        _preferences.AdbPath = AdbPathInput.Text.Trim().Trim('"');
        _preferences.AdbSerial = SerialInput.Text.Trim();
        _preferences.SshHost = SshHostInput.Text.Trim();
        _preferences.SshKey = SshKeyInput.Text.Trim().Trim('"');
        _preferences.SshKnownHosts = SshKnownHostsInput.Text.Trim().Trim('"');
    }
    private async void RecoverUsb_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        ReadConnectionInputs();
        var result = await _connection.RecoverUsbAsync();
        await _preferences.SaveAsync();
        _nextConnectionRead = 0;
        return result.Trim();
    });

    private async Task ActionAsync(Func<Task<string>> action, bool reportSuccess = true)
    {
        if (_actionPending || _disposed) return;
        _actionPending = true;
        RefreshCodecButtons();
        PatchButton.IsEnabled = RestoreButton.IsEnabled = false;
        AdvancedApplyButton.IsEnabled = AdvancedRestoreButton.IsEnabled = false;
        await _actions.WaitAsync();
        try { var message = await action(); if (reportSuccess) ShowStatus(message, false); }
        catch (Exception ex) { ShowStatus(ex.Message, true, offerElevation: !Elevation.IsElevated && (ex is ElevationSuggestedException || Elevation.IsAccessDenied(ex))); }
        finally
        {
            _actions.Release();
            _actionPending = false;
            RefreshCodecButtons();
            UpdatePatchButtons();
            // Patch buttons are refreshed from the verified file state after each operation.
            if (reportSuccess) await PollAsync();
        }
    }
    private void ShowStatus(string message, bool error, bool offerElevation = false)
    {
        ActionStatus.ActionButton = offerElevation ? Elevation.CreateRestartButton(XamlRoot) : null;
        if (offerElevation && !message.Contains("administrator", StringComparison.OrdinalIgnoreCase))
            message += " Access was denied; restarting as administrator may be required.";
        ActionStatus.Message = message;
        ActionStatus.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        ActionStatus.IsOpen = true;
    }

    private async Task<string> InspectPatchAsync()
    {
        _patch = await _patcher.InspectAsync(DriverPathInput.Text.Trim().Trim('"'));
        BindPatch();
        return _patch.Message;
    }
    private void BindPatch()
    {
        if (_patch == null) return;
        PatchStatus.Text = _patch.Message;
        PatchDetails.Text = $"File: {_patch.FilePath}\nSHA-256: {_patch.Sha256 ?? "unavailable"}\nBackup: {_patch.BackupPath}\nBackup status: {_patch.BackupState}\nPatch receipt: {_patch.ReceiptPath}";
        UpdatePatchButtons();
    }
    private void UpdatePatchButtons()
    {
        if (_actionPending) return;
        PatchButton.IsEnabled = _patch?.CanApply == true && !_vrRunning;
        RestoreButton.IsEnabled = _patch?.CanRestore == true && !_vrRunning;
    }
    private async void InspectPatch_Click(object sender, RoutedEventArgs e) => await ActionAsync(InspectPatchAsync, reportSuccess: false);
    private async void InstallPatch_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        _patch = await _patcher.ApplyAsync(DriverPathInput.Text.Trim().Trim('"'));
        BindPatch();
        return _patch.Message;
    });
    private async void RestorePatch_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        _patch = await _patcher.RestoreAsync(DriverPathInput.Text.Trim().Trim('"'));
        BindPatch();
        return _patch.Message;
    });

    private sealed class ElevationSuggestedException(string message) : InvalidOperationException(message);

    private void Graph_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();
    private void DrawGraph()
    {
        RateGraph.Children.Clear();
        double width = RateGraph.ActualWidth, height = RateGraph.ActualHeight;
        if (width < 1 || height < 1) return;
        var end = Now;
        var maximum = Math.Max(100, Math.Ceiling((_history.SelectMany(s => new[] { s.Video ?? 0, s.Transport ?? 0, s.Target ?? 0 }).DefaultIfEmpty(0).Max() + 20) / 50) * 50);
        GraphScale.Text = $"0–{maximum:0} Mbps";
        for (int i = 0; i <= 4; i++)
            RateGraph.Children.Add(new Line { X1 = 0, X2 = width, Y1 = height * i / 4, Y2 = height * i / 4, StrokeThickness = 1, Stroke = new SolidColorBrush(Color.FromArgb(255, 42, 42, 46)) });
        DrawSeries(s => s.Target, Color.FromArgb(255, 140, 140, 150), true);
        DrawSeries(s => s.Transport, Color.FromArgb(255, 92, 189, 118), false);
        DrawSeries(s => s.Video, Color.FromArgb(255, 26, 159, 255), false);
        void DrawSeries(Func<(double At, double? Video, double? Transport, double? Target), double?> select, Color color, bool dashed)
        {
            Polyline? line = null;
            double lastAt = 0;
            foreach (var sample in _history)
            {
                var value = select(sample);
                if (value is null || !double.IsFinite(value.Value)) { line = null; continue; }
                if (line is null || sample.At - lastAt > 5)
                {
                    line = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = dashed ? 1 : 2, StrokeLineJoin = PenLineJoin.Round };
                    if (dashed) line.StrokeDashArray = new DoubleCollection { 5, 5 };
                    RateGraph.Children.Add(line);
                }
                line.Points.Add(new Point(Math.Clamp((sample.At - end + 60) / 60 * width, 0, width), height - Math.Clamp(value.Value / maximum, 0, 1) * height));
                lastAt = sample.At;
            }
        }
    }
}
