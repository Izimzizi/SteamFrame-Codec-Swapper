using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamLinkVRSettings.Controls;
using SteamLinkVRSettings.Frame;
using SteamLinkVRSettings.Services;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private sealed class AdvancedRow
    {
        public required string Key;
        public required CheckBox Override;
        public NumberBox? Number;
        public ToggleSwitch? Toggle;
        public ComboBox? Choice;
        public TextBlock? Readback;
        public long Revision;
        public bool Dirty;
    }
    private readonly Dictionary<string, AdvancedRow> _encoderRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AdvancedRow> _driverRows = new(StringComparer.Ordinal);
    private bool _syncingAdvanced = true;
    private long _encoderAdvancedRevision;
    private bool _encoderAdvancedEdited;
    private bool _advancedAvailable;
    private double _nextAdvancedRead;

    private TextBlock AdvancedHint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Resources["Hint"] };
    private AdvancedRow AddAdvancedRow(StackPanel host, string key, string label, string hint, double? minimum = null,
        double maximum = 0, double step = 1, double initial = 0, bool boolean = false, string[]? choices = null, bool driver = false)
    {
        var container = new StackPanel { Spacing = 4 };
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(title);
        var check = new CheckBox { Content = "Override", VerticalAlignment = VerticalAlignment.Center, MinWidth = 104 };
        Grid.SetColumn(check, 2); grid.Children.Add(check);
        var row = new AdvancedRow { Key = key, Override = check };
        Control control;
        if (choices is not null)
        {
            row.Choice = new NoWheelComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var choice in choices) row.Choice.Items.Add(choice);
            row.Choice.SelectedIndex = 0;
            row.Choice.SelectionChanged += (_, _) => AdvancedEdited(row, driver);
            control = row.Choice;
        }
        else if (boolean)
        {
            row.Toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsOn = initial != 0 };
            row.Toggle.Toggled += (_, _) => AdvancedEdited(row, driver);
            control = row.Toggle;
        }
        else
        {
            row.Number = new NoWheelNumberBox { Minimum = minimum ?? 0, Maximum = maximum, SmallChange = step,
                LargeChange = step * 4, Value = initial, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            row.Number.ValueChanged += (_, _) => AdvancedEdited(row, driver);
            control = row.Number;
        }
        control.IsEnabled = false;
        Grid.SetColumn(control, 1); grid.Children.Add(control);
        check.Checked += (_, _) => { control.IsEnabled = true; AdvancedEdited(row, driver); };
        check.Unchecked += (_, _) => { control.IsEnabled = false; AdvancedEdited(row, driver); };
        container.Children.Add(grid); container.Children.Add(AdvancedHint(hint));
        if (driver) { row.Readback = AdvancedHint("Readback: not yet read"); container.Children.Add(row.Readback); }
        host.Children.Add(container);
        (driver ? _driverRows : _encoderRows).Add(key, row);
        return row;
    }
    private void InitializeAdvancedControls()
    {
        AddAdvancedRow(AdvancedEncoderHost, "cq", "VBR quality target (CQ)", "0 is automatic. Lower nonzero values request less quantization. VBR only; bitrate and buffer limits can prevent the target from being met.", 0, 51, .25, 30);
        AddAdvancedRow(AdvancedEncoderHost, "maxqp", "Maximum QP (I / P / B frames)", "Rate-control QP limit for all frame types. Valve's delta map and shared budget still affect gaze quality; this is not an absolute center-QP guarantee.", 0, 51, 1, 40);
        AddAdvancedRow(AdvancedEncoderHost, "peak", "Peak bitrate (Mbps)", "Custom peak also caps Valve's transient encoder average. This is an encoder budget, not an instantaneous network-traffic limit. Higher peaks can overwhelm the wireless/decoder budget.", 25, 500, 5, 230);
        AddAdvancedRow(AdvancedEncoderHost, "vbv", "VBV buffer budget (Mbit)", "Encoder rate-control buffer budget; this is not the headset's decoded-frame queue.", .001, 64, .1, 2);
        AddAdvancedRow(AdvancedEncoderHost, "delay", "VBV initial delay budget (Mbit)", "Initial VBV budget in bits, not a measured display-delay time. Keep it within the configured buffer.", 0, 64, .1, 2);
        AddAdvancedRow(AdvancedEncoderHost, "cb", "Cb chroma QP offset", "Reconnect required. Negative values allocate more bits to the blue-difference channel; chroma offsets are applied as a pair.", -12, 12, 1, 0);
        AddAdvancedRow(AdvancedEncoderHost, "cr", "Cr chroma QP offset", "Reconnect required. Negative values allocate more bits to the red-difference channel; use both Override boxes together.", -12, 12, 1, 0);
        AddAdvancedRow(AdvancedEncoderHost, "spatial", "Spatial adaptive quantization", "Reconnect required. Experimental with Valve's delta-QP map; API acceptance and actual picture/stutter determine usefulness.", boolean: true);
        AddAdvancedRow(AdvancedEncoderHost, "strength", "Spatial AQ strength", "Used only when the spatial-AQ override is enabled and spatial AQ is on.", 0, 15, 1, 8);
        AddAdvancedRow(AdvancedEncoderHost, "temporal", "Temporal adaptive quantization", "Reconnect required. Experimental allocation across frames; preserve the gaze map and compare timing before keeping it.", boolean: true);
        AddAdvancedRow(AdvancedEncoderHost, "preset", "NVENC preset", "Reconnect required. Preset query and initialization are paired with Valve's stream policy preserved; only a bounded quality-field whitelist is inherited. This is not a full generic preset configuration. P3 previously had no demonstrated benefit.", choices: ["P1", "P2", "P3", "P4", "P5", "P6", "P7"]);
        foreach (var definition in AdvancedDriverSettings.Definitions)
            AddAdvancedRow(AdvancedDriverHost, definition.Key, definition.Label, definition.Hint + (definition.Reconnect ? " Reconnect required." : ""),
                definition.Minimum, definition.Maximum, definition.Step, definition.Minimum, definition.Kind == DriverValueKind.Boolean, driver: true);
        SyncAdvancedEncoder(_preferences.AdvancedEncoding ?? new());
        _syncingAdvanced = false;
    }
    private void AdvancedEdited(AdvancedRow row, bool driver)
    {
        if (_syncingAdvanced) return;
        row.Revision++; row.Dirty = true;
        if (!driver) { _encoderAdvancedRevision++; _encoderAdvancedEdited = true; }
    }
    private void SyncAdvancedEncoder(AdvancedEncoding request)
    {
        _syncingAdvanced = true;
        try
        {
            Number("cq", request.QualityTarget); Number("maxqp", request.MaximumQp); Number("peak", request.PeakMbps);
            Number("vbv", request.VbvMbit); Number("delay", request.VbvDelayMbit); Number("cb", request.CbQpOffset); Number("cr", request.CrQpOffset);
            Bool("spatial", request.SpatialAq); Bool("temporal", request.TemporalAq);
            Number("strength", request.SpatialAq == true ? request.AqStrength : null);
            var preset = _encoderRows["preset"]; preset.Override.IsChecked = request.Preset is > 0;
            if (request.Preset is > 0) preset.Choice!.SelectedIndex = request.Preset.Value - 1;
        }
        finally { _syncingAdvanced = false; }
        void Number(string key, double? value) { var row = _encoderRows[key]; row.Override.IsChecked = value.HasValue; if (value.HasValue) row.Number!.Value = value.Value; }
        void Bool(string key, bool? value) { var row = _encoderRows[key]; row.Override.IsChecked = value.HasValue; if (value.HasValue) row.Toggle!.IsOn = value.Value; }
    }
    private AdvancedEncoding ReadAdvancedEncoderInputs()
    {
        double? Number(string key) => _encoderRows[key].Override.IsChecked == true ? _encoderRows[key].Number!.Value : null;
        int? Integer(string key)
        {
            var value = Number(key);
            if (value.HasValue && (!double.IsFinite(value.Value) || value != Math.Truncate(value.Value))) throw new ArgumentException("Use a whole-number value for " + key + ".");
            return value.HasValue ? checked((int)value.Value) : null;
        }
        bool? Bool(string key) => _encoderRows[key].Override.IsChecked == true ? _encoderRows[key].Toggle!.IsOn : null;
        var request = new AdvancedEncoding
        {
            QualityTarget = Number("cq"), MaximumQp = Integer("maxqp"), PeakMbps = Number("peak"), VbvMbit = Number("vbv"), VbvDelayMbit = Number("delay"),
            CbQpOffset = Integer("cb"), CrQpOffset = Integer("cr"), SpatialAq = Bool("spatial"), TemporalAq = Bool("temporal"),
            AqStrength = _encoderRows["strength"].Override.IsChecked == true ? Integer("strength") ?? 8 : 8,
            Preset = _encoderRows["preset"].Override.IsChecked == true ? _encoderRows["preset"].Choice!.SelectedIndex + 1 : null
        };
        if (_encoderRows["strength"].Override.IsChecked == true && request.SpatialAq != true)
            throw new ArgumentException("AQ strength requires spatial AQ Override to be on.");
        request.Validate(); return request.Canonicalize();
    }
    private async Task RefreshAdvancedDriverAsync(bool force = false)
    {
        if (!force && Now < _nextAdvancedRead) return;
        _nextAdvancedRead = Now + 5;
        var service = new SettingsService();
        var overrides = await service.LoadUserOverridesAsync();
        var merged = await service.LoadMergedAsync();
        var live = _vrRunning ? await ReadOpenVrAsync("advanced", () => OpenVrSettings.ReadAdvanced(_paths)) : null;
        _syncingAdvanced = true;
        try
        {
            foreach (var definition in AdvancedDriverSettings.Definitions)
            {
                var row = _driverRows[definition.Key];
                merged.TryGetValue(definition.Key, out var saved);
                var readback = live?.GetValueOrDefault(definition.Key);
                var current = readback?.Value ?? saved;
                row.Readback!.Text = live is null ? $"Saved value: {FormatSetting(saved)}" : readback!.Available
                    ? $"OpenVR readback: {FormatSetting(readback.Value)} • {(overrides.ContainsKey(definition.Key) ? "user override" : "default")}" : readback.Message;
                if (row.Dirty || ContainsFocus(row.Override) || row.Number is not null && ContainsFocus(row.Number) || row.Toggle is not null && ContainsFocus(row.Toggle)) continue;
                row.Override.IsChecked = overrides.ContainsKey(definition.Key);
                if (current is bool boolean) row.Toggle!.IsOn = boolean;
                else if (current is not null && row.Number is not null) row.Number.Value = Convert.ToDouble(current);
            }
        }
        finally { _syncingAdvanced = false; }
    }
    private static string FormatSetting(object? value) => value switch { null => "unset; driver fallback", bool b => b ? "on" : "off", double d => d.ToString("0.###"), _ => value.ToString() ?? "unset" };

    private async void ApplyAdvanced_Click(object sender, RoutedEventArgs e)
    {
        // A loaded preset owns the complete setup, including any edits on this page.
        if (_pendingPreset != null) { Apply_Click(sender, e); return; }
        await ApplyAdvancedSettingsAsync();
    }
    private Task ApplyAdvancedSettingsAsync() => ActionAsync(async () =>
    {
        await EnsureCodecStableAsync();
        var request = ReadAdvancedEncoderInputs(); var revision = _encoderAdvancedRevision;
        // Explicit Apply can retry a previously rejected experiment with the
        // same values after the user fixes its context (for example, selects VBR).
        var hevc = SelectedCodec == StreamCodec.Hevc;
        var encoderEdited = hevc && (_encoderAdvancedEdited || _advancedAvailable && !request.IsDefault);
        var writes = new Dictionary<string, object?>(); var removes = new List<string>(); var rows = new Dictionary<string, long>();
        foreach (var definition in AdvancedDriverSettings.Definitions)
        {
            var row = _driverRows[definition.Key]; if (!row.Dirty) continue;
            rows[definition.Key] = row.Revision;
            if (row.Override.IsChecked != true) { removes.Add(definition.Key); continue; }
            object raw = definition.Kind == DriverValueKind.Boolean ? row.Toggle!.IsOn : row.Number!.Value;
            definition.Validate(raw);
            writes[definition.Key] = definition.Kind == DriverValueKind.Integer ? checked((int)row.Number!.Value) : raw;
        }
        await ValidateWidthBoundsAsync(writes, removes);
        var running = await Task.Run(() => OpenVrSettings.IsRunning);
        if (hevc && _policyEdited)
        {
            var policy = new EncodingPolicy((EncoderRateControl)RateControlInput.SelectedIndex, (EncoderMultiPass)MultiPassInput.SelectedIndex);
            _ = policy.Packed;
            if (running)
            {
                var beforePolicy = await _probe.GetSnapshotAsync();
                if (beforePolicy.PairedCodecTrialPresent) throw new InvalidOperationException("Encoder overrides are available for HEVC. Select HEVC in Stream settings first.");
                if (!beforePolicy.Installed) await _probe.StartAsync();
                await ApplyPolicyAsync(policy);
            }
            _preferences.RateControl = policy.RateControl; _preferences.MultiPass = policy.MultiPass;
            _preferences.ReapplyAfterRestart = ReapplyInput.IsChecked == true;
            await _preferences.SaveAsync(); _policyEdited = false;
        }
        if (running && encoderEdited) ValidateAdvancedAgainstObserved(request, await _probe.GetSnapshotAsync());
        if (writes.Count != 0 || removes.Count != 0)
        {
            if (running) { await Task.Run(() => OpenVrSettings.RemoveKeys(_paths, removes)); await Task.Run(() => OpenVrSettings.Apply(_paths, writes)); }
            else await new SettingsService().SaveUserOverridesAsync(writes, removes);
        }
        string message = "Advanced driver settings accepted" + (running ? " by OpenVR." : " for the next SteamVR session.");
        if (running && AdvancedDriverSettings.Definitions.Any(d => d.Reconnect && (writes.ContainsKey(d.Key) || removes.Contains(d.Key))))
            message += " Reconnect to activate changes to encoder or sender initialization settings.";
        if (encoderEdited)
        {
            message += await ApplyAdvancedEncoderAsync(request, running);
            _preferences.AdvancedEncoding = request; await _preferences.SaveAsync();
            if (revision == _encoderAdvancedRevision) { _encoderAdvancedEdited = false; foreach (var row in _encoderRows.Values) row.Dirty = false; }
        }
        foreach (var item in rows) if (_driverRows[item.Key].Revision == item.Value) _driverRows[item.Key].Dirty = false;
        _preferences.ReapplyAfterRestart = ReapplyInput.IsChecked == true;
        await _preferences.SaveAsync();
        await RefreshAdvancedDriverAsync(force: true);
        return message;
    });
    private async Task ValidateWidthBoundsAsync(Dictionary<string, object?> writes, List<string> removes)
    {
        if (!writes.Keys.Concat(removes).Any(key => AdvancedDriverSettings.WidthBoundKeys.Contains(key, StringComparer.Ordinal))) return;
        var service = new SettingsService(); var merged = await service.LoadMergedAsync(); var defaults = await service.LoadDefaultsAsync();
        if (await Task.Run(() => OpenVrSettings.IsRunning))
        {
            var live = await Task.Run(() => OpenVrSettings.ReadAdvanced(_paths));
            foreach (var key in AdvancedDriverSettings.WidthBoundKeys)
            {
                var readback = live[key];
                if (readback.Available) merged[key] = readback.Value;
                else if (readback.ErrorCode == 5) { merged.Remove(key); defaults.Remove(key); } // Current runtime fallback wins over stale disk values.
                else if (!writes.ContainsKey(key) && !removes.Contains(key))
                    throw new IOException($"Cannot confirm the current {key}: {readback.Message} Override both bounds or retry when OpenVR is available.");
            }
        }
        AdvancedDriverSettings.ValidateWidthBounds(merged, defaults, writes, removes);
    }
    private async Task<string> ApplyAdvancedEncoderAsync(AdvancedEncoding request, bool running)
    {
        if (running) await EnsureCodecStableAsync();
        if (!running) return " Encoder request saved; start SteamVR and apply it during a supported NVENC session.";
        var before = await _probe.GetSnapshotAsync();
        if (!before.Installed) before = await _probe.StartAsync();
        if (!before.Installed || before.AdvancedEncoding is null) return " Encoder request pending: " + before.Message + " The updated helper requires a SteamVR restart.";
        var result = await _probe.SetAdvancedEncodingAsync(request);
        if (result.AdvancedEncoding?.Requested != request) return " Encoder request was not accepted: " + result.Message;
        if (result.Active)
        {
            await Task.Run(() => OpenVrSettings.RefreshEncoder(_paths));
            result = await _probe.GetSnapshotAsync();
        }
        return result.AdvancedEncoding is { } advanced ? $" Encoder request submitted. Pending flags: {advanced.PendingFlags}; rejected flags: {advanced.RejectedFlags}. If reconnect retains the encoder, use Restart SteamVR on the Frame as needed, then restart PC SteamVR with this app open and Reapply enabled. Connect the Frame after requests are armed." : " Encoder advanced readback unavailable.";
    }
    private static void ValidateAdvancedAgainstObserved(AdvancedEncoding request, EncoderSnapshot snapshot)
    {
        if (snapshot.AdvancedEncoding is { CapabilitiesQueried: true } capabilities)
        {
            if ((request.VbvMbit.HasValue || request.VbvDelayMbit.HasValue) && !capabilities.CustomVbvSupported)
                throw new ArgumentException("This encoder does not advertise custom VBV support. Leave VBV overrides unchecked.");
            if (request.TemporalAq == true && !capabilities.TemporalAqSupported)
                throw new ArgumentException("This encoder does not advertise temporal AQ support.");
        }
        if (!snapshot.Active || snapshot.EncodingPolicy is not { Observed: true } policy) return;
        if ((request.QualityTarget.HasValue || request.PeakMbps.HasValue) && policy.RateControl != 1)
            throw new ArgumentException("CQ and separate peak-bitrate overrides require VBR. Select VBR under Encoding method and apply again.");
        var buffer = request.VbvMbit is double vbv ? vbv * 1_000_000 : policy.VbvSize;
        var delay = request.VbvDelayMbit is double initial ? initial * 1_000_000 : policy.VbvDelay;
        if ((request.VbvMbit.HasValue || request.VbvDelayMbit.HasValue) && delay > buffer)
            throw new ArgumentException("VBV initial delay cannot exceed the accepted or requested buffer; override both if reducing the buffer.");
    }
    private async void RestoreAdvanced_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        await EnsureCodecStableAsync();
        var revision = _encoderAdvancedRevision; var rows = _driverRows.ToDictionary(p => p.Key, p => p.Value.Revision);
        var running = await Task.Run(() => OpenVrSettings.IsRunning);
        var keys = AdvancedDriverSettings.Definitions.Select(d => d.Key).ToArray();
        if (running) await Task.Run(() => OpenVrSettings.RemoveKeys(_paths, keys)); else await new SettingsService().SaveUserOverridesAsync([], keys);
        var message = await ApplyAdvancedEncoderAsync(new(), running);
        _preferences.AdvancedEncoding = new(); await _preferences.SaveAsync();
        if (revision == _encoderAdvancedRevision) { _encoderAdvancedEdited = false; foreach (var row in _encoderRows.Values) row.Dirty = false; SyncAdvancedEncoder(new()); }
        foreach (var item in rows) if (_driverRows[item.Key].Revision == item.Value) _driverRows[item.Key].Dirty = false;
        await RefreshAdvancedDriverAsync(force: true);
        return "Advanced driver overrides removed; Valve defaults/fallbacks apply." + message;
    });
    private void RenderAdvancedReadbacks(EncoderSnapshot snapshot)
    {
        _advancedAvailable = snapshot.Installed && snapshot.AdvancedEncoding is { Supported: true };
        foreach (var row in _encoderRows.Values)
        {
            var editable = SelectedCodec == StreamCodec.Hevc && !_actionPending && !_codecSwitching;
            row.Override.IsEnabled = editable;
            if (row.Number is not null) row.Number.IsEnabled = editable && row.Override.IsChecked == true;
            if (row.Toggle is not null) row.Toggle.IsEnabled = editable && row.Override.IsChecked == true;
            if (row.Choice is not null) row.Choice.IsEnabled = editable && row.Override.IsChecked == true;
        }
        AdvancedCapabilityStatus.Text = _advancedAvailable ? "Updated helper available. Initialization-only fields need a new encoder session. If pending, restart Frame/PC SteamVR as needed; keep this app open with Reapply enabled and let requests arm before Frame reconnect." +
            (snapshot.AdvancedEncoding!.CapabilitiesQueried ? $" Queried NVENC capabilities: dynamic bitrate {snapshot.AdvancedEncoding.DynamicBitrateSupported}, custom VBV {snapshot.AdvancedEncoding.CustomVbvSupported}, temporal AQ {snapshot.AdvancedEncoding.TemporalAqSupported}." : " NVENC capability query pending.")
            : snapshot.PairedCodecTrialPresent ? "The paired codec trial controls the encoder. App encoder overrides are disabled while it is loaded."
            : "Advanced encoder controls require the updated helper and a supported NVENC stream. Start the monitor; restart SteamVR after a helper update. Driver controls remain available.";
        var advanced = snapshot.AdvancedEncoding;
        if (advanced is null)
        {
            AdvancedRequestedReadback.Text = snapshot.PairedCodecTrialPresent ? "App advanced encoder overrides: disabled for the paired codec trial."
                : "Requested advanced encoding: unavailable (updated helper required)";
            AdvancedAcceptedReadback.Text = "Last successfully submitted configuration: unavailable";
            AdvancedLiveReadback.Text = "Advanced encoder configuration: unavailable";
        }
        else
        {
            if (!_encoderAdvancedEdited && !_encoderRows.Values.Any(row => ContainsFocus(row.Override) || row.Number is not null && ContainsFocus(row.Number) || row.Toggle is not null && ContainsFocus(row.Toggle) || row.Choice is not null && ContainsFocus(row.Choice)))
                SyncAdvancedEncoder(advanced.Requested);
            foreach (var key in new[] { "vbv", "delay" })
            {
                var row = _encoderRows[key]; var supported = !advanced.CapabilitiesQueried || advanced.CustomVbvSupported;
                row.Override.IsEnabled = _advancedAvailable && (supported || row.Override.IsChecked == true);
                row.Number!.IsEnabled = _advancedAvailable && supported && row.Override.IsChecked == true;
            }
            AdvancedRequestedReadback.Text = $"Requested overrides: {advanced.Requested.Flags}. Pending: {advanced.PendingFlags}; rejected: {advanced.RejectedFlags}; last NVENC rejection: {advanced.LastError}.";
            AdvancedAcceptedReadback.Text = snapshot.EncodingPolicy is { Observed: true } ?
                $"Last successful submitted config: CQ {advanced.QualityTarget:0.###}; max QP {(advanced.MaximumQpEnabled ? $"I {advanced.MaximumQpI} / P {advanced.MaximumQpP} / B {advanced.MaximumQpB}" : "disabled")}; Cb {advanced.CbQpOffset}, Cr {advanced.CrQpOffset}; spatial AQ {(advanced.SpatialAq ? "on" : "off")} strength {advanced.AqStrength}; temporal AQ {(advanced.TemporalAq ? "on" : "off")}.\n" +
                $"Applied override flags: {advanced.AppliedFlags}. Peak {snapshot.EncodingPolicy.MaxBitrate / 1_000_000.0:0.0} Mbps; VBV {snapshot.EncodingPolicy.VbvSize / 1_000_000.0:0.00}/{snapshot.EncodingPolicy.VbvDelay / 1_000_000.0:0.00} Mbit; preset {snapshot.EncodingPolicy.Preset}." + (snapshot.Active ? "" : " Stream inactive; readback is historical.")
                : "Last successfully submitted configuration: not yet observed. These values are API submission readbacks, not an NVIDIA effective-configuration getter.";
            AdvancedLiveReadback.Text = $"Advanced overrides: applied {advanced.AppliedFlags}; pending {advanced.PendingFlags}; rejected {advanced.RejectedFlags}.";
        }
        var timing = snapshot.CallbackTiming;
        CallbackTimingReadback.Text = timing is null ? "Encoder API duration: unavailable" :
            $"Encoder API durations since monitor start: reconfigure mean {(timing.ReconfigureCalls > 0 ? timing.ReconfigureTotalUs / (double)timing.ReconfigureCalls / 1000 : 0):0.000} ms, max {timing.ReconfigureMaximumUs / 1000.0:0.000} ms, {timing.ReconfigureOver2Ms} calls >2 ms. " +
            $"Encode mean {(timing.EncodeCalls > 0 ? timing.EncodeTotalUs / (double)timing.EncodeCalls / 1000 : 0):0.000} ms, max {timing.EncodeMaximumUs / 1000.0:0.000} ms, {timing.EncodeOver2Ms} calls >2 ms. " +
            $"Submit-to-output completion mean {(timing.CompletionCalls > 0 ? timing.CompletionTotalUs / (double)timing.CompletionCalls / 1000 : 0):0.000} ms, max {timing.CompletionMaximumUs / 1000.0:0.000} ms. Completion includes encoder/queue/lock, excludes network/decoder.";
    }
}
