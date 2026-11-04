using System.Globalization;
using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>
/// The benchmark window: choose what to run (bitrate steps, or the smoothest encoder settings), press Start, watch the results fill in, and compare saved runs on charts.
/// It only shows and asks; <see cref="TrayApp"/> runs the benchmark.
/// </summary>
public sealed class BenchmarkForm : Form
{
    private static readonly (string Text, int Width)[] BitrateColumns =
    [
        ("Target", 62), ("Asked of encoder", 100), ("Encoded", 78), ("FPS", 50), ("Held /min", 72), ("PC drops", 66), ("PC late", 62), ("Encode ms (avg / max)", 150), ("QP", 48), ("Result", 78), ("Notes", 320)
    ];

    private static readonly (string Text, int Width)[] SweepColumns =
    [
        ("Tried", 230), ("Encoded", 78), ("FPS", 50), ("Held ms/min", 90), ("Held frames", 80), ("Resets", 56), ("PC drops", 66), ("Encode ms (avg / max)", 150), ("Result", 78), ("What happened", 320)
    ];

    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    private readonly NumericUpDown _start = Number(25, 500, 100), _ceiling = Number(25, 500, 500), _step = Number(10, 200, 50), _probe = Number(25, 500, 200);
    private readonly ComboBox _measure = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly CheckBox _stopOnFailure = new() { Text = "Stop after the first failed step", Checked = true, AutoSize = true };
    private readonly CheckBox _scene = new() { Text = "Show the built-in moving test scene on the headset (no game needed)", Checked = true, AutoSize = true };
    private readonly FlowLayoutPanel _bitrateOptions = new() { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 4), MaximumSize = new Size(1060, 0) };
    private readonly FlowLayoutPanel _sweepOptions = new() { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 4), MaximumSize = new Size(1060, 0), Visible = false };
    private readonly Button _go = new() { Text = "Start", AutoSize = true, MinimumSize = new Size(90, 30) };
    private readonly Button _apply = new() { Text = "Apply best settings", AutoSize = true, MinimumSize = new Size(170, 34), Visible = false, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly Label _best = new() { AutoSize = true, MaximumSize = new Size(900, 0), Margin = new Padding(10, 8, 0, 0), Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), Visible = false };
    private readonly Button _delete = new() { Text = "Delete selected run", Dock = DockStyle.Fill, MinimumSize = new Size(90, 28) };
    private readonly Label _detail = new() { AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(4, 6, 4, 4) };
    private readonly Button _openFolder = new() { Text = "Open results folder", AutoSize = true, MinimumSize = new Size(90, 30) };
    private readonly Label _estimate = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _phase = new() { AutoSize = true, Text = "Ready." };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, Height = 14 };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ListView _table = new() { View = View.Details, FullRowSelect = true, GridLines = true, Dock = DockStyle.Fill, HideSelection = false };
    private readonly RunChart _chart = new() { Dock = DockStyle.Fill };
    private readonly CheckedListBox _compare = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly TextBox _conclusion = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 120, BackColor = SystemColors.Window };

    private List<BenchmarkRun> _history = [];
    private List<EncoderSweepRun> _sweepHistory = [];
    private BenchmarkRun? _shown;
    private EncoderSweepRun? _shownSweep;
    private EncoderChoice? _recommended, _applied;
    private bool _running, _probeTouched, _updatingList;

    public event Action<BenchmarkSettings>? StartRequested;
    public event Action<EncoderSweepSettings, bool>? SweepRequested;
    public event Action<EncoderChoice>? ApplyRequested;
    public event Action? StopRequested;
    public event Action<object>? DeleteRequested;

    public bool Running => _running;
    private bool SweepMode => _mode.SelectedIndex >= 1;
    private EncoderGoal Goal => _mode.SelectedIndex switch { 2 => EncoderGoal.Picture, 3 => EncoderGoal.Current, _ => EncoderGoal.Smoothest };
    private Label _sweepLead = null!, _sweepTail = null!;

    public BenchmarkForm(bool driverPatched)
    {
        Text = "Benchmark";
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 760);
        MinimumSize = new Size(900, 600);
        ShowIcon = false;
        _scene.Enabled = BenchmarkScene.Available;
        if (!BenchmarkScene.Available) _scene.Checked = false;
        _ceiling.Value = driverPatched ? WirelessAdvisor.PatchedCeilingMbps : WirelessAdvisor.StockCeilingMbps;
        _measure.Items.AddRange(["15 s", "24 s", "45 s"]);
        _measure.SelectedIndex = 1;
        _mode.Items.AddRange(["Bitrate steps — how high can the link go?", "Best encoder settings — the smoothest at one bitrate", "Best encoder settings — the best picture that still streams well", "Test my current settings — one measurement, nothing is changed"]);
        _mode.SelectedIndex = 0;

        var intro = new Label
        {
            AutoSize = true, MaximumSize = new Size(1060, 0), Margin = new Padding(0, 0, 0, 8),
            Text = "The run asks the Frame to stay awake and, with the built-in test scene ticked, shows a moving high-detail room on the headset (it replaces whatever VR app is on screen; untick it to test with your own busy game and stay in it). " +
                   "Bitrate steps raises the target step by step and judges each one. Best encoder settings tries the preset, rate control, multipass and VBV at a single bitrate, starting at the lightest values and working up, and keeps what streams smoothest (or, for the best-picture goal, what delivers the most of the bitrate while still streaming acceptably). Test my current settings measures what you use now, once, at your current bitrate target, and changes nothing. Your own settings are put back afterwards."
        };
        var modeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
        modeRow.Controls.AddRange([Caption("Run:"), _mode, Caption("Measure each for"), _measure, _scene]);
        _bitrateOptions.Controls.AddRange([Caption("From"), _start, Caption("to"), _ceiling, Caption("Mbps in steps of"), _step, Caption("Mbps"), _stopOnFailure]);
        _sweepOptions.Controls.AddRange([_sweepLead = Caption("Measure every candidate at"), _probe, _sweepTail = Caption("Mbps. Pick a bitrate a little below where your link starts to fail (about 85% of a bitrate run's highest clean step, which is the default after a bitrate run): right at the limit every change tips the stream into recovery, and at an easy bitrate every setting looks the same.")]);
        var optionsHost = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
        optionsHost.Controls.AddRange([_bitrateOptions, _sweepOptions]);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        actions.Controls.AddRange([_go, _openFolder, _estimate]);
        var bottom = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        bottom.Controls.AddRange([_apply, _best]);
        _estimate.Margin = new Padding(12, 8, 0, 0);

        var tablePage = new TabPage("Results") { Padding = new Padding(4) };
        tablePage.Controls.Add(_table);
        var chartPage = new TabPage("Charts") { Padding = new Padding(4) };
        var side = new TableLayoutPanel { Dock = DockStyle.Right, Width = 440, ColumnCount = 1, RowCount = 4 };
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize)); side.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); side.RowStyles.Add(new RowStyle(SizeType.AutoSize)); side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.Controls.Add(new Label { Text = "Tick the runs to draw on the charts. The first entry is the run on screen. Click one to read its full name and best settings.", AutoSize = true, MaximumSize = new Size(430, 0), Margin = new Padding(4, 4, 4, 6) }, 0, 0);
        _compare.HorizontalScrollbar = true;
        side.Controls.Add(_compare, 0, 1);
        side.Controls.Add(_detail, 0, 2);
        side.Controls.Add(_delete, 0, 3);
        chartPage.Controls.Add(_chart);
        chartPage.Controls.Add(side);
        _tabs.TabPages.AddRange([tablePage, chartPage]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(12, 10, 12, 10) };
        for (var i = 0; i < 6; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(intro, 0, 0); root.Controls.Add(modeRow, 0, 1); root.Controls.Add(optionsHost, 0, 2); root.Controls.Add(actions, 0, 3);
        root.Controls.Add(_phase, 0, 4); root.Controls.Add(_bar, 0, 5); root.Controls.Add(_tabs, 0, 6); root.Controls.Add(_conclusion, 0, 7); root.Controls.Add(bottom, 0, 8);
        Controls.Add(root);

        // The status line wraps over as many lines as the text needs, and the full text is also a tooltip.
        var phaseTip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 300 };
        void FitPhase() => _phase.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 30), 0);
        _phase.TextChanged += (_, _) => phaseTip.SetToolTip(_phase, _phase.Text);
        Resize += (_, _) => FitPhase();
        FitPhase();

        UseColumns(BitrateColumns);
        _go.Click += (_, _) => { if (_running) StopRequested?.Invoke(); else Start(); };
        _apply.Click += (_, _) =>
        {
            if (_recommended is not { } choice) return;
            _apply.Enabled = false; _apply.Text = "Applying…";
            ApplyRequested?.Invoke(choice);
        };
        _delete.Click += (_, _) => DeleteSelected();
        _compare.SelectedIndexChanged += (_, _) => ShowSelectedDetail();
        _openFolder.Click += (_, _) => { try { Directory.CreateDirectory(AppData.BenchmarksDirectory); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppData.BenchmarksDirectory) { UseShellExecute = true }); } catch (Exception) { } };
        foreach (var control in new Control[] { _start, _ceiling, _step, _probe, _measure }) { if (control is NumericUpDown n) n.ValueChanged += (_, _) => UpdateEstimate(); else ((ComboBox)control).SelectedIndexChanged += (_, _) => UpdateEstimate(); }
        _probe.ValueChanged += (_, _) => _probeTouched = true;
        _mode.SelectedIndexChanged += (_, _) => ModeChanged();
        _compare.ItemCheck += (_, e) => { if (!_updatingList) BeginInvoke(RefreshCharts); };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            if (_running) StopRequested?.Invoke();
            Hide();
        };
        UpdateEstimate();
    }

    private static NumericUpDown Number(int minimum, int maximum, int value) => new() { Minimum = minimum, Maximum = maximum, Value = value, Increment = 10, Width = 64 };
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(6, 6, 2, 0), MaximumSize = new Size(1000, 0) };

    private TimeSpan Measure => TimeSpan.FromSeconds(_measure.SelectedIndex switch { 0 => 15, 2 => 45, _ => 24 });

    private void UseColumns((string Text, int Width)[] columns)
    {
        _table.Items.Clear();
        _table.Columns.Clear();
        foreach (var (text, width) in columns) _table.Columns.Add(text, width);
        _table.Columns[^1].Width = -2;
    }

    private void ModeChanged()
    {
        _bitrateOptions.Visible = !SweepMode;
        _sweepOptions.Visible = SweepMode;
        var current = Goal == EncoderGoal.Current;
        _probe.Visible = _sweepTail.Visible = !current;
        _sweepLead.Text = current ? "Measures the encoder settings you use now at your current bitrate target, once (the length chosen above), and changes nothing. Start it whenever you like." : "Measure every candidate at";
        UseColumns(SweepMode ? SweepColumns : BitrateColumns);
        _conclusion.Text = "";
        _bar.Value = 0;
        _phase.Text = "Ready.";
        if (SweepMode && _shownSweep is { Trials.Count: > 0 } sweep) ShowSweep(sweep, null); else if (!SweepMode && _shown is { Steps.Count: > 0 } run) ShowRun(run, null);
        RebuildCompareList();
        UpdateBest(SweepMode ? _shownSweep : _shown);
        UpdateEstimate();
    }

    private BenchmarkSettings? Current()
    {
        if (_ceiling.Value < _start.Value) return null;
        return new((int)_start.Value, (int)_ceiling.Value, (int)_step.Value, LinkCapacitySweep.SettleTime, Measure, _stopOnFailure.Checked, _scene.Checked && BenchmarkScene.Available);
    }

    private EncoderSweepSettings CurrentSweep() => new((int)_probe.Value, TimeSpan.FromSeconds(6), Measure, Goal);

    private void UpdateEstimate()
    {
        if (SweepMode)
        {
            var sweep = CurrentSweep();
            if (sweep.Goal == EncoderGoal.Current) { _estimate.Text = $"one measurement, about {Math.Ceiling(sweep.Estimate.TotalSeconds / 10) * 10:0} seconds"; return; }
            _estimate.Text = $"{sweep.CandidateCount} settings and a final check, plus a repeat for each one that looks better; about {Math.Ceiling(sweep.Estimate.TotalMinutes):0} minutes";
            return;
        }
        if (Current() is not { } settings) { _estimate.Text = "The last target must not be below the first."; return; }
        var steps = settings.Targets.Count;
        _estimate.Text = $"{steps} step{(steps == 1 ? "" : "s")}, about {Math.Ceiling(settings.Estimate.TotalMinutes):0} minutes";
    }

    private void Start()
    {
        if (SweepMode) { SweepRequested?.Invoke(CurrentSweep(), _scene.Checked && BenchmarkScene.Available); return; }
        if (Current() is not { } settings) { UpdateEstimate(); return; }
        StartRequested?.Invoke(settings);
    }

    /// <summary>The bitrate the sweep starts with (the user's current target), unless the user already chose one.</summary>
    public void SetProbe(int mbps)
    {
        if (InvokeRequired) { BeginInvoke(() => SetProbe(mbps)); return; }
        if (_probeTouched) return;
        _probe.Value = Math.Clamp(mbps, (int)_probe.Minimum, (int)_probe.Maximum);
        _probeTouched = false;
    }

    /// <summary>The run is starting or has ended: the button turns into Stop and back.</summary>
    public void SetRunning(bool running)
    {
        if (InvokeRequired) { BeginInvoke(() => SetRunning(running)); return; }
        _running = running;
        _go.Text = running ? "Stop" : "Start";
        foreach (var control in new Control[] { _mode, _start, _ceiling, _step, _probe, _measure, _stopOnFailure, _scene }) control.Enabled = !running && (control != _scene || BenchmarkScene.Available);
        _delete.Enabled = !running;
        _phase.ForeColor = SystemColors.ControlText;
        if (!running) UpdateBest(SweepMode ? _shownSweep : _shown);
        if (running)
        {
            _table.Items.Clear(); _conclusion.Text = ""; _bar.Value = 0;
            UpdateBest(null);
            if (SweepMode) _shownSweep = new EncoderSweepRun { At = DateTimeOffset.Now }; else _shown = new BenchmarkRun { At = DateTimeOffset.Now };
            RebuildCompareList();
        }
    }

    public void ShowStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowStatus(text)); return; }
        _phase.Text = text;
    }

    public void SetHistory(List<BenchmarkRun> history)
    {
        if (InvokeRequired) { BeginInvoke(() => SetHistory(history)); return; }
        _history = history;
        if (!_probeTouched && history.FirstOrDefault(r => r.BestCleanMbps is not null) is { BestCleanMbps: int clean })
        {
            // Below the edge: right at the highest clean step every change of settings tips the stream into a recovery storm and the sweep measures that.
            _probe.Value = Math.Clamp((int)Math.Round(clean * 0.85 / 5) * 5, (int)_probe.Minimum, (int)_probe.Maximum);
            _probeTouched = false;
        }
        RebuildCompareList();
    }

    public void SetSweepHistory(List<EncoderSweepRun> history)
    {
        if (InvokeRequired) { BeginInvoke(() => SetSweepHistory(history)); return; }
        _sweepHistory = history;
        RebuildCompareList();
    }

    // ----- bitrate steps -----

    public void ShowProgress(BenchmarkProgress progress)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowProgress(progress)); return; }
        _phase.Text = progress.Phase == "done"
            ? $"Step {progress.StepIndex + 1} of {progress.StepCount} ({progress.TargetMbps} Mbps) finished."
            : $"Step {progress.StepIndex + 1} of {progress.StepCount}: {progress.TargetMbps} Mbps — {progress.Phase}, {progress.PhaseSecondsLeft:0} s left";
        _bar.Value = Math.Clamp((int)((progress.StepIndex + (progress.Phase == "done" ? 1 : 0)) * 1000.0 / Math.Max(1, progress.StepCount)), 0, 1000);
        if (progress.Finished is { } step)
        {
            AddRow(step);
            _shown?.Steps.Add(step);
            if (_shown?.Steps.Count == 1) RebuildCompareList(); else RefreshCharts();
        }
    }

    private void AddRow(BenchmarkStep step)
    {
        var item = new ListViewItem(step.TargetMbps + " Mbps");
        item.SubItems.AddRange(
        [
            step.AskedMbps is double asked ? $"{asked:0} Mbps" : "—", step.MeanMbps is double mbps ? $"{mbps:0} Mbps" : "—", step.MeanFps is double fps ? fps.ToString("0.0", CultureInfo.InvariantCulture) : "—",
            step.HeldPerMinute.ToString("0", CultureInfo.InvariantCulture), step.PcDropped.ToString(CultureInfo.InvariantCulture), step.PcLate.ToString(CultureInfo.InvariantCulture),
            step.EncodeAverageMs is double avg ? $"{avg:0.0} / {step.EncodeMaxMs:0.0}" : "—", step.AverageQp is double qp ? qp.ToString("0.0", CultureInfo.InvariantCulture) : "—",
            step.Verdict.ToString(), step.Reason
        ]);
        item.ForeColor = VerdictColor(step.Verdict);
        _table.Items.Add(item);
    }

    private static Color VerdictColor(SweepVerdict verdict) => verdict switch { SweepVerdict.Clean => Color.FromArgb(30, 130, 60), SweepVerdict.Marginal => Color.FromArgb(180, 120, 0), _ => Color.FromArgb(190, 40, 40) };

    /// <summary>The run has ended (or been stopped): show every step again as measured and the conclusion.</summary>
    public void ShowRun(BenchmarkRun run, string? savedTo)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowRun(run, savedTo)); return; }
        _shown = run;
        _phase.Text = run.Stopped ? "Stopped." : run.Error is null ? "Finished." : "Ended: " + run.Error;
        _bar.Value = run.Stopped || run.Error is not null ? _bar.Value : _bar.Maximum;
        if (SweepMode) return;
        UpdateBest(run);
        _table.Items.Clear();
        foreach (var step in run.Steps) AddRow(step);
        _conclusion.Text = run.Conclusion + (savedTo is null ? "" : Environment.NewLine + Environment.NewLine + "Saved: " + savedTo);
        RebuildCompareList();
        if (run.Steps.Count > 0) _tabs.SelectedIndex = 1;
    }

    // ----- encoder sweep -----

    public void ShowSweepProgress(EncoderSweepProgress progress)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowSweepProgress(progress)); return; }
        _phase.Text = progress.Phase == "done"
            ? $"{progress.Candidate} finished ({progress.Done} of {progress.Total} windows)."
            : $"{progress.Candidate} — {progress.Phase}, {progress.SecondsLeft:0} s left ({progress.Done + 1} of about {progress.Total} windows)";
        _bar.Value = Math.Clamp((int)(progress.Done * 1000.0 / Math.Max(1, progress.Total)), 0, 1000);
        if (progress.Finished is { } trial)
        {
            AddTrialRow(trial);
            _shownSweep?.Trials.Add(trial);
            if (_shownSweep?.Trials.Count == 1) RebuildCompareList(); else RefreshCharts();
        }
    }

    private void AddTrialRow(EncoderTrial trial)
    {
        var step = trial.Step;
        var item = new ListViewItem(trial.Label);
        item.SubItems.AddRange(
        [
            step.MeanMbps is double mbps ? $"{mbps:0} Mbps" : "—", step.MeanFps is double fps ? fps.ToString("0.0", CultureInfo.InvariantCulture) : "—",
            step.HeldMsPerMinute.ToString("0", CultureInfo.InvariantCulture), step.HeldFrames.ToString(CultureInfo.InvariantCulture), step.StreamResets.ToString(CultureInfo.InvariantCulture), step.PcDropped.ToString(CultureInfo.InvariantCulture),
            step.EncodeAverageMs is double avg ? $"{avg:0.0} / {step.EncodeMaxMs:0.0}" : "—", step.Verdict.ToString(),
            // A failed value says why, so it never reads as "rough" with nothing behind it.
            step.Verdict == SweepVerdict.Failed && step.Reason.Length > 0 && step.Reason != "not tried" ? trial.Decision + " — " + step.Reason : trial.Decision
        ]);
        item.ForeColor = VerdictColor(step.Verdict);
        if (trial.Adopted) item.Font = new Font(_table.Font, FontStyle.Bold);
        _table.Items.Add(item);
    }

    public void ShowSweep(EncoderSweepRun run, string? savedTo)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowSweep(run, savedTo)); return; }
        _shownSweep = run;
        _phase.Text = run.Stopped ? "Stopped." : run.Error is null ? "Finished." : "Ended: " + run.Error;
        _bar.Value = run.Stopped || run.Error is not null ? _bar.Value : _bar.Maximum;
        if (!SweepMode) return;
        UpdateBest(run);
        _table.Items.Clear();
        foreach (var trial in run.Trials) AddTrialRow(trial);
        _conclusion.Text = run.Conclusion + (savedTo is null ? "" : Environment.NewLine + Environment.NewLine + "Saved: " + savedTo);
        RebuildCompareList();
        if (run.Trials.Count > 0) _tabs.SelectedIndex = 1;
    }

    // ----- best result, selection and deleting -----

    /// <summary>The run's best result in plain sight at the bottom left, with the button that puts it into force.</summary>
    private void UpdateBest(object? run)
    {
        _recommended = null;
        string? text = null;
        switch (run)
        {
            case EncoderSweepRun { Goal: EncoderGoal.Current, Trials.Count: > 0 } measured:
                text = "Your current settings: " + measured.Trials[0].Decision + " at " + measured.ProbeMbps + " Mbps";
                break;
            case EncoderSweepRun { Best: { } best } sweep:
                text = (sweep.Goal == EncoderGoal.Picture ? "Best picture at " : "Smoothest settings at ") + sweep.ProbeMbps + " Mbps: " + best.Describe() + (best == sweep.Baseline ? " (what you use now)" : "");
                if (best != sweep.Baseline) _recommended = best;
                break;
            case BenchmarkRun { BestCleanMbps: int clean }:
                text = "Highest clean bitrate: " + clean + " Mbps";
                break;
        }
        _best.Text = text ?? "";
        _best.Visible = text is not null;
        _apply.Visible = _recommended is not null && !_running;
        ShowApplyState();
    }

    /// <summary>The button says what happened: ready, applied (green, inactive) or, after a failure, ready again.</summary>
    private void ShowApplyState()
    {
        var applied = _recommended is not null && _recommended == _applied;
        _apply.Text = applied ? "✓ Applied" : "Apply best settings";
        _apply.Enabled = !applied;
        _apply.ForeColor = applied ? Color.FromArgb(30, 130, 60) : SystemColors.ControlText;
    }

    /// <summary>The tray reports whether the settings were put into force.</summary>
    public void ShowApplyResult(EncoderChoice choice, bool success, string message)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowApplyResult(choice, success, message)); return; }
        if (success) _applied = choice;
        ShowApplyState();
        _phase.Text = (success ? "Applied: " : "Not applied: ") + message;
        _phase.ForeColor = success ? Color.FromArgb(30, 130, 60) : Color.FromArgb(190, 40, 40);
    }

    private void ShowSelectedDetail()
    {
        if (_compare.SelectedItem is not ListEntry entry) { _detail.Text = ""; return; }
        var best = entry.Run switch { EncoderSweepRun { Best: { } b } => Environment.NewLine + "Best settings: " + b.Describe(), BenchmarkRun { BestCleanMbps: int clean } => Environment.NewLine + "Highest clean bitrate: " + clean + " Mbps", _ => "" };
        _detail.Text = entry.Text + best;
        if (!_running) UpdateBest(entry.Run);
    }

    private void DeleteSelected()
    {
        if (_running || _compare.SelectedItem is not ListEntry entry) return;
        if (MessageBox.Show(this, "Delete this run and its saved files?" + Environment.NewLine + Environment.NewLine + entry.Text, "Delete run", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        DeleteRequested?.Invoke(entry.Run);
        switch (entry.Run)
        {
            case BenchmarkRun run:
                _history = _history.Where(r => r.At != run.At).ToList();
                if (_shown?.At == run.At) { _shown = null; if (!SweepMode) _table.Items.Clear(); }
                break;
            case EncoderSweepRun sweep:
                _sweepHistory = _sweepHistory.Where(r => r.At != sweep.At).ToList();
                if (_shownSweep?.At == sweep.At) { _shownSweep = null; if (SweepMode) _table.Items.Clear(); }
                break;
        }
        _detail.Text = "";
        UpdateBest(SweepMode ? _shownSweep : _shown);
        RebuildCompareList();
    }

    // ----- charts and comparison -----

    private static string RunName(BenchmarkRun run)
    {
        var width = run.Setup.FirstOrDefault(l => l.StartsWith("stream width", StringComparison.Ordinal));
        return $"{run.At.LocalDateTime:MMM d HH:mm} {run.Codec}" + (width is null ? "" : " " + width.Replace("stream width ", "") + "px") + (run.BestCleanMbps is int clean ? $" (clean to {clean})" : "");
    }

    private static string RunName(EncoderSweepRun run) => $"{run.At.LocalDateTime:MMM d HH:mm} {run.Codec} at {run.ProbeMbps} Mbps" + (run.Best is { } best ? $" → {best.Describe()}" : "");

    /// <summary>The run on screen first (ticked), then every saved run of this kind; the two newest are ticked when nothing is on screen.</summary>
    private void RebuildCompareList()
    {
        _updatingList = true;
        _compare.Items.Clear();
        if (SweepMode)
        {
            var showing = _shownSweep is { Trials.Count: > 0 };
            if (showing) _compare.Items.Add(new ListEntry("This run — " + RunName(_shownSweep!), _shownSweep!), true);
            foreach (var run in _sweepHistory.Where(r => r.At != _shownSweep?.At)) _compare.Items.Add(new ListEntry(RunName(run), run), _compare.Items.Count < 2);
        }
        else
        {
            var showing = _shown is { Steps.Count: > 0 };
            if (showing) _compare.Items.Add(new ListEntry("This run — " + RunName(_shown!), _shown!), true);
            foreach (var run in _history.Where(r => r.At != _shown?.At)) _compare.Items.Add(new ListEntry(RunName(run), run), _compare.Items.Count < 2);
        }
        _updatingList = false;
        RefreshCharts();
    }

    private sealed record ListEntry(string Text, object Run) { public override string ToString() => Text; }

    private void RefreshCharts()
    {
        var picked = new List<ListEntry>();
        for (var i = 0; i < _compare.Items.Count; i++)
            if (_compare.GetItemChecked(i)) picked.Add((ListEntry)_compare.Items[i]);
        var series = new List<ChartSeries>();
        for (var i = 0; i < picked.Count; i++)
        {
            var color = ChartData.Palette[i % ChartData.Palette.Length];
            var name = picked[i].Text.Length > 70 ? picked[i].Text[..70] + "…" : picked[i].Text;
            switch (picked[i].Run)
            {
                case BenchmarkRun run: series.Add(new ChartSeries(name, color, ChartData.Of(run))); break;
                case EncoderSweepRun sweep: series.Add(new ChartSeries(name, color, ChartData.Of(sweep))); break;
            }
        }
        _chart.Show(series, bars: SweepMode);
        // Two bitrate runs ticked: say in words which one is better, target by target.
        if (!_running && !SweepMode && picked.Count == 2 && picked[0].Run is BenchmarkRun a && picked[1].Run is BenchmarkRun b)
            _conclusion.Text = (a.Conclusion.Length > 0 ? a.Conclusion + Environment.NewLine + Environment.NewLine : "") + BenchmarkReport.Compare(a, b);
    }
}
