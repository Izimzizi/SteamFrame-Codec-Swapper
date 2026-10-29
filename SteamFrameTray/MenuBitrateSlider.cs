namespace SteamFrameTray;

/// <summary>
/// A bitrate slider that sits inside the tray menu. It covers 200-500 Mbps (SteamVR's own slider already reaches 350),
/// snaps to every 50 Mbps when close to one, shows the value being dragged without waiting for the mouse to be released
/// and applies it on release.
/// </summary>
public sealed class MenuBitrateSlider : ToolStripControlHost
{
    public const int Minimum = 200, Maximum = 500, Snap = 50, SnapReach = 8, MenuWidth = 300;
    private readonly TrackBar _slider;
    private readonly ToolStripLabel _label;
    private bool _syncing, _dragging;
    private DateTime _holdUntil;

    public MenuBitrateSlider(ToolStripLabel label, Func<int, Task> apply) : base(new TrackBar
    {
        Minimum = Minimum, Maximum = Maximum, TickFrequency = Snap, SmallChange = 5, LargeChange = Snap,
        AutoSize = false, Size = new Size(MenuWidth - 12, 40), TickStyle = TickStyle.BottomRight,
    })
    {
        _label = label;
        _slider = (TrackBar)Control;
        AutoSize = false; Size = new Size(MenuWidth, 44); Padding = new Padding(6, 0, 6, 0);
        _slider.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            var snapped = Snapped(_slider.Value);
            if (snapped != _slider.Value) { _syncing = true; try { _slider.Value = snapped; } finally { _syncing = false; } }
            _label.Text = Caption(snapped);
        };
        _slider.MouseDown += (_, _) => _dragging = true;
        _slider.MouseUp += async (_, _) => { _dragging = false; _holdUntil = DateTime.UtcNow.AddSeconds(3); await apply(_slider.Value); };
        _slider.KeyUp += async (_, _) => { _holdUntil = DateTime.UtcNow.AddSeconds(3); await apply(_slider.Value); };
    }

    /// <summary>Rounds to 5 Mbps and pulls values within a few Mbps of a multiple of 50 onto it.</summary>
    public static int Snapped(int mbps)
    {
        var value = Math.Clamp((int)Math.Round(mbps / 5.0) * 5, Minimum, Maximum);
        var nearest = (int)Math.Round(value / (double)Snap) * Snap;
        return Math.Abs(value - nearest) <= SnapReach && nearest is >= Minimum and <= Maximum ? nearest : value;
    }

    private static string Caption(int mbps) => $"Bitrate: {mbps} Mbps";

    /// <summary>Shows the controller's value, unless the user is moving the slider or has just let go of it.</summary>
    public void SetValue(int mbps)
    {
        if (_dragging || DateTime.UtcNow < _holdUntil) return;
        _label.Text = Caption(mbps);
        if (_slider.Focused && _slider.Value == mbps) return;
        _syncing = true;
        try { _slider.Value = Math.Clamp(mbps, Minimum, Maximum); } finally { _syncing = false; }
    }
}
