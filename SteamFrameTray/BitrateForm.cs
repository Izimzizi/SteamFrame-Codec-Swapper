namespace SteamFrameTray;

/// <summary>
/// The bitrate slider. It shows SteamVR's own target (the tray follows SteamVR's slider) and applies a new value
/// shortly after the user stops moving it.
/// </summary>
public sealed class BitrateForm : Form
{
    private const int Step = 5;
    private readonly TrayController _controller;
    private readonly Func<int, Task> _apply;
    private readonly TrackBar _slider = new() { Minimum = TrayController.MinimumMbps, Maximum = TrayController.MaximumMbps, TickFrequency = 25, SmallChange = Step, LargeChange = 25, Dock = DockStyle.Fill };
    private readonly NumericUpDown _number = new() { Minimum = TrayController.MinimumMbps, Maximum = TrayController.MaximumMbps, Increment = Step, Width = 70 };
    private readonly Label _state = new() { AutoSize = false, Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 450 };
    private bool _dragging, _applying, _syncing;
    private int _lastApplied;

    public BitrateForm(TrayController controller, Func<int, Task> apply)
    {
        _controller = controller; _apply = apply;
        Text = "Bitrate";
        Icon = TrayIcons.For(TrayState.Disarmed);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false; TopMost = true; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(400, 120);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 10, 10, 0) };
        top.Controls.AddRange([new Label { Text = "Target bitrate", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, _number, new Label { Text = "Mbps", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }]);
        _state.Padding = new Padding(10, 0, 10, 6);
        _state.Height = 40;
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        bottom.Controls.Add(_state);
        Controls.Add(_slider); Controls.Add(top); Controls.Add(bottom);

        _slider.ValueChanged += (_, _) => { if (!_syncing) { SetNumber(_slider.Value); Schedule(); } };
        _slider.MouseDown += (_, _) => _dragging = true;
        _slider.MouseUp += (_, _) => { _dragging = false; Schedule(); };
        _number.ValueChanged += (_, _) => { if (!_syncing) { SetSlider((int)_number.Value); Schedule(); } };
        _debounce.Tick += async (_, _) => await ApplyAsync();
        _controller.Changed += OnChanged;
        Shown += (_, _) => Pull();
        FormClosed += (_, _) => { _controller.Changed -= OnChanged; _debounce.Dispose(); };
    }

    public void ShowNear(Point screenPoint)
    {
        var area = Screen.FromPoint(screenPoint).WorkingArea;
        Location = new Point(Math.Clamp(screenPoint.X - Width / 2, area.Left, area.Right - Width), Math.Clamp(screenPoint.Y - Height - 20, area.Top, area.Bottom - Height));
        if (!Visible) Show();
        Activate();
        Pull();
    }

    private void Schedule() { _debounce.Stop(); if (!_dragging) _debounce.Start(); }

    private async Task ApplyAsync()
    {
        _debounce.Stop();
        var value = (int)_number.Value;
        if (_dragging || _applying || value == _lastApplied) return;
        _applying = true;
        try { await _apply(value); }
        finally { _applying = false; Pull(); }
    }

    private void OnChanged(TraySnapshot snapshot)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnChanged(snapshot)); return; }
        Pull();
    }

    /// <summary>Shows the controller's current value unless the user is in the middle of choosing one.</summary>
    private void Pull()
    {
        var snapshot = _controller.Current;
        if (!_dragging && !_applying && !_debounce.Enabled)
        {
            _lastApplied = snapshot.TargetMbps;
            SetNumber(snapshot.TargetMbps); SetSlider(snapshot.TargetMbps);
        }
        _state.Text = snapshot.IsArmed ? "Armed H.264: changes apply live and follow SteamVR's slider."
            : _controller.SteamVrRunning ? "SteamVR is running: changes apply live and follow SteamVR's slider."
            : "SteamVR is closed: the value is written to its settings for the next start.";
        if ((int)_number.Value > TrayController.PatchFreeMaxMbps) _state.Text += $" Above {TrayController.PatchFreeMaxMbps} Mbps needs the bitrate patch.";
    }

    private void SetNumber(int value) { _syncing = true; try { _number.Value = Math.Clamp(value, TrayController.MinimumMbps, TrayController.MaximumMbps); } finally { _syncing = false; } }
    private void SetSlider(int value) { _syncing = true; try { _slider.Value = Math.Clamp(value, TrayController.MinimumMbps, TrayController.MaximumMbps); } finally { _syncing = false; } }
}
