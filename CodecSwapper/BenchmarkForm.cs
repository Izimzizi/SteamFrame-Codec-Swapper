using System.Globalization;
using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>
/// The bitrate benchmark window: choose the range, press Start, watch the steps fill in, read the conclusion and compare with an earlier run.
/// It only shows and asks; <see cref="TrayApp"/> runs the benchmark.
/// </summary>
public sealed class BenchmarkForm : Form
{
    private static readonly (string Text, int Width)[] Columns =
    [
        ("Target", 62), ("Encoded", 78), ("FPS", 50), ("Held /min", 72), ("PC drops", 66), ("PC late", 62), ("Encode ms (avg / max)", 150), ("QP", 48), ("Result", 78), ("Notes", 320)
    ];

    private readonly NumericUpDown _start = Number(25, 500, 100), _ceiling = Number(25, 500, 500), _step = Number(10, 200, 50);
    private readonly ComboBox _measure = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly CheckBox _stopOnFailure = new() { Text = "Stop after the first failed step", Checked = true, AutoSize = true };
    private readonly CheckBox _scene = new() { Text = "Show the built-in moving test scene on the headset (no game needed)", Checked = true, AutoSize = true };
    private readonly Button _go = new() { Text = "Start", AutoSize = true, MinimumSize = new Size(90, 30) };
    private readonly Button _openFolder = new() { Text = "Open results folder", AutoSize = true, MinimumSize = new Size(90, 30) };
    private readonly Label _estimate = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _phase = new() { AutoSize = true, Text = "Ready." };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, Height = 14 };
    private readonly ListView _table = new() { View = View.Details, FullRowSelect = true, GridLines = true, Dock = DockStyle.Fill, HideSelection = false };
    private readonly TextBox _conclusion = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 120, BackColor = SystemColors.Window };
    private readonly ComboBox _earlier = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private List<BenchmarkRun> _history = [];
    private BenchmarkRun? _shown;
    private bool _running;

    /// <summary>Raised when Start is pressed with valid settings.</summary>
    public event Action<BenchmarkSettings>? StartRequested;
    public event Action? StopRequested;

    public bool Running => _running;

    public BenchmarkForm(bool driverPatched)
    {
        Text = "Bitrate benchmark";
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1000, 640);
        MinimumSize = new Size(820, 520);
        ShowIcon = false;
        _scene.Enabled = BenchmarkScene.Available;
        if (!BenchmarkScene.Available) _scene.Checked = false;
        _ceiling.Value = driverPatched ? WirelessAdvisor.PatchedCeilingMbps : WirelessAdvisor.StockCeilingMbps;
        _measure.Items.AddRange(["15 s", "24 s", "45 s"]);
        _measure.SelectedIndex = 1;

        var intro = new Label
        {
            AutoSize = true, MaximumSize = new Size(960, 0), Margin = new Padding(0, 0, 0, 8),
            Text = "Keep the headset awake. With the built-in test scene ticked, a moving high-detail room is shown for you (it replaces whatever VR app is on screen); untick it to test with your own busy game instead, and stay in it until the run ends. The benchmark raises the bitrate target step by step with the codec and encoder settings " +
                   "that are selected now, measures held-back frames, PC drops, encode time and the rate actually delivered at each step, and judges each one. Your own bitrate target is put back afterwards."
        };
        var options = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
        options.Controls.AddRange([Caption("From"), _start, Caption("to"), _ceiling, Caption("Mbps in steps of"), _step, Caption("Mbps, measure each for"), _measure, _stopOnFailure, _scene]);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        actions.Controls.AddRange([_go, _openFolder, _estimate]);
        _estimate.Margin = new Padding(12, 8, 0, 0);

        foreach (var (text, width) in Columns) _table.Columns.Add(text, width);
        _table.Columns[^1].Width = -2;

        var compare = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        compare.Controls.AddRange([Caption("Compare with an earlier run:"), _earlier]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(12, 10, 12, 10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        root.Controls.Add(intro, 0, 0); root.Controls.Add(options, 0, 1); root.Controls.Add(actions, 0, 2);
        root.Controls.Add(_phase, 0, 3); root.Controls.Add(_bar, 0, 4); root.Controls.Add(_table, 0, 5); root.Controls.Add(compare, 0, 6); root.Controls.Add(_conclusion, 0, 7);
        Controls.Add(root);

        _go.Click += (_, _) => { if (_running) StopRequested?.Invoke(); else Start(); };
        _openFolder.Click += (_, _) => { try { Directory.CreateDirectory(AppData.BenchmarksDirectory); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppData.BenchmarksDirectory) { UseShellExecute = true }); } catch (Exception) { } };
        foreach (var control in new Control[] { _start, _ceiling, _step, _measure }) { if (control is NumericUpDown n) n.ValueChanged += (_, _) => UpdateEstimate(); else ((ComboBox)control).SelectedIndexChanged += (_, _) => UpdateEstimate(); }
        _earlier.SelectedIndexChanged += (_, _) => ShowComparison();
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
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(6, 6, 2, 0) };

    private TimeSpan Measure => TimeSpan.FromSeconds(_measure.SelectedIndex switch { 0 => 15, 2 => 45, _ => 24 });

    private BenchmarkSettings? Current()
    {
        if (_ceiling.Value < _start.Value) return null;
        return new((int)_start.Value, (int)_ceiling.Value, (int)_step.Value, LinkCapacitySweep.SettleTime, Measure, _stopOnFailure.Checked, _scene.Checked && BenchmarkScene.Available);
    }

    private void UpdateEstimate()
    {
        if (Current() is not { } settings) { _estimate.Text = "The last target must not be below the first."; return; }
        var steps = settings.Targets.Count;
        _estimate.Text = $"{steps} step{(steps == 1 ? "" : "s")}, about {Math.Ceiling(settings.Estimate.TotalMinutes):0} minutes";
    }

    private void Start()
    {
        if (Current() is not { } settings) { UpdateEstimate(); return; }
        StartRequested?.Invoke(settings);
    }

    /// <summary>The run is starting or has ended: the button turns into Stop and back.</summary>
    public void SetRunning(bool running)
    {
        if (InvokeRequired) { BeginInvoke(() => SetRunning(running)); return; }
        _running = running;
        _go.Text = running ? "Stop" : "Start";
        foreach (var control in new Control[] { _start, _ceiling, _step, _measure, _stopOnFailure, _scene, _earlier }) control.Enabled = !running;
        if (running) { _table.Items.Clear(); _conclusion.Text = ""; _shown = null; _bar.Value = 0; }
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
        _earlier.Items.Clear();
        _earlier.Items.Add("(none)");
        foreach (var run in history) _earlier.Items.Add(run.Title);
        _earlier.SelectedIndex = 0;
    }

    public void ShowProgress(BenchmarkProgress progress)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowProgress(progress)); return; }
        _phase.Text = progress.Phase == "done"
            ? $"Step {progress.StepIndex + 1} of {progress.StepCount} ({progress.TargetMbps} Mbps) finished."
            : $"Step {progress.StepIndex + 1} of {progress.StepCount}: {progress.TargetMbps} Mbps — {progress.Phase}, {progress.PhaseSecondsLeft:0} s left";
        _bar.Value = Math.Clamp((int)((progress.StepIndex + (progress.Phase == "done" ? 1 : 0)) * 1000.0 / Math.Max(1, progress.StepCount)), 0, 1000);
        if (progress.Finished is { } step) AddRow(step);
    }

    private void AddRow(BenchmarkStep step)
    {
        var item = new ListViewItem(step.TargetMbps + " Mbps");
        item.SubItems.AddRange(
        [
            step.MeanMbps is double mbps ? $"{mbps:0} Mbps" : "—", step.MeanFps is double fps ? fps.ToString("0.0", CultureInfo.InvariantCulture) : "—",
            step.HeldPerMinute.ToString("0", CultureInfo.InvariantCulture), step.PcDropped.ToString(CultureInfo.InvariantCulture), step.PcLate.ToString(CultureInfo.InvariantCulture),
            step.EncodeAverageMs is double avg ? $"{avg:0.0} / {step.EncodeMaxMs:0.0}" : "—", step.AverageQp is double qp ? qp.ToString("0.0", CultureInfo.InvariantCulture) : "—",
            step.Verdict.ToString(), step.Reason
        ]);
        item.ForeColor = step.Verdict switch { SweepVerdict.Clean => Color.FromArgb(30, 130, 60), SweepVerdict.Marginal => Color.FromArgb(180, 120, 0), _ => Color.FromArgb(190, 40, 40) };
        _table.Items.Add(item);
    }

    /// <summary>The run has ended (or been stopped): show every step again as measured and the conclusion.</summary>
    public void ShowRun(BenchmarkRun run, string? savedTo)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowRun(run, savedTo)); return; }
        _shown = run;
        _phase.Text = run.Stopped ? "Stopped." : run.Error is null ? "Finished." : "Ended: " + run.Error;
        _bar.Value = run.Stopped || run.Error is not null ? _bar.Value : _bar.Maximum;
        _table.Items.Clear();
        foreach (var step in run.Steps) AddRow(step);
        _conclusion.Text = run.Conclusion + (savedTo is null ? "" : Environment.NewLine + Environment.NewLine + "Saved: " + savedTo);
    }

    private void ShowComparison()
    {
        if (_shown is null) return;
        if (_earlier.SelectedIndex <= 0) { _conclusion.Text = _shown.Conclusion; return; }
        var other = _history[_earlier.SelectedIndex - 1];
        _conclusion.Text = _shown.Conclusion + Environment.NewLine + Environment.NewLine + BenchmarkReport.Compare(_shown, other);
    }
}
