using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace SteamFrameTray;

/// <summary>"Start with Windows" through the per-user Run key (no administrator needed).</summary>
public static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SteamFrameTray";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (!enabled) { key.DeleteValue(ValueName, false); return; }
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The app path is unknown.");
        key.SetValue(ValueName, $"\"{path}\"");
    }
}

public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Starts a new elevated copy; the caller then exits this one. Returns false if the user declined the prompt.</summary>
    public static bool RestartElevated()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The app path is unknown.");
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" }); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}

/// <summary>Tray icons drawn at runtime, one per state, so the app ships no image assets.</summary>
public static class TrayIcons
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    private static readonly Dictionary<TrayState, Icon> Cache = [];

    public static Icon For(TrayState state)
    {
        if (Cache.TryGetValue(state, out var cached)) return cached;
        var color = state switch
        {
            TrayState.Armed => Color.FromArgb(46, 160, 67),
            TrayState.Waiting => Color.FromArgb(47, 129, 247),
            TrayState.Arming or TrayState.Disarming => Color.FromArgb(210, 153, 34),
            TrayState.Error => Color.FromArgb(218, 54, 51),
            _ => Color.FromArgb(110, 118, 129)
        };
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var fill = new SolidBrush(color);
            graphics.FillEllipse(fill, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("H", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }
        var handle = bitmap.GetHicon();
        try { return Cache[state] = (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
}

/// <summary>A small modal text prompt (password or number).</summary>
public sealed class PromptDialog : Form
{
    private readonly TextBox _input = new() { Dock = DockStyle.Top };

    private PromptDialog(string title, string message, bool password, string initial)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(380, 132);
        _input.UseSystemPasswordChar = password;
        _input.Text = initial;
        var label = new Label { Text = message, Dock = DockStyle.Top, Height = 58, Padding = new Padding(0, 0, 0, 6) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Location = new Point(212, 96) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Location = new Point(296, 96) };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0) };
        body.Controls.Add(_input);
        body.Controls.Add(label);
        Controls.Add(body);
        Controls.Add(ok);
        Controls.Add(cancel);
        ok.BringToFront(); cancel.BringToFront();
        AcceptButton = ok; CancelButton = cancel;
    }

    public static string? Ask(string title, string message, bool password = false, string initial = "")
    {
        using var dialog = new PromptDialog(title, message, password, initial);
        dialog.TopMost = true;
        return dialog.ShowDialog() == DialogResult.OK ? dialog._input.Text : null;
    }
}
