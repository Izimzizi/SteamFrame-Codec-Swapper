namespace CodecSwapper;

/// <summary>
/// A stream-width slider that sits inside the tray menu, under the bitrate one. It runs from 1344 (the most SteamLink's own slider
/// reaches) up to <see cref="StreamWidth.Maximum"/> in steps of 64 pixels, shows the value being dragged and applies it on release.
/// </summary>
public sealed class MenuWidthSlider : ToolStripControlHost
{
    private readonly TrackBar _slider;
    private readonly ToolStripLabel _label;
    private bool _syncing, _dragging;
    private DateTime _holdUntil;

    public MenuWidthSlider(ToolStripLabel label, Func<int, Task> apply) : base(new TrackBar
    {
        Minimum = StreamWidth.Minimum, Maximum = StreamWidth.Maximum, TickFrequency = 128, SmallChange = StreamWidth.Step, LargeChange = 128,
        AutoSize = false, Size = new Size(MenuBitrateSlider.MenuWidth - 12, 40), TickStyle = TickStyle.BottomRight,
    })
    {
        _label = label;
        _slider = (TrackBar)Control;
        AutoSize = false; Size = new Size(MenuBitrateSlider.MenuWidth, 44); Padding = new Padding(6, 0, 6, 0);
        _slider.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            var snapped = StreamWidth.Snapped(_slider.Value);
            if (snapped != _slider.Value) { _syncing = true; try { _slider.Value = snapped; } finally { _syncing = false; } }
            _label.Text = Caption(snapped);
        };
        _slider.MouseDown += (_, _) => _dragging = true;
        _slider.MouseUp += async (_, _) => { _dragging = false; _holdUntil = DateTime.UtcNow.AddSeconds(3); await apply(_slider.Value); };
        _slider.KeyUp += async (_, _) => { _holdUntil = DateTime.UtcNow.AddSeconds(3); await apply(_slider.Value); };
    }

    private static string Caption(int width) => $"Stream width: {width} px";

    /// <summary>Shows SteamVR's current width, unless the user is moving the slider or has just let go of it.</summary>
    public void SetValue(int width)
    {
        if (_dragging || DateTime.UtcNow < _holdUntil) return;
        _label.Text = Caption(width);
        _syncing = true;
        try { _slider.Value = Math.Clamp(width, StreamWidth.Minimum, StreamWidth.Maximum); } finally { _syncing = false; }
    }
}
