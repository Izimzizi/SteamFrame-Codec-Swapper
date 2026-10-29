using SteamLinkVRSettings.Frame;

namespace SteamFrameTray;

/// <summary>The one-time connection check: a list of steps with plain results and one action for whatever is next.</summary>
public sealed class SetupForm : Form
{
    private readonly SetupCheck _check;
    private readonly FramePreferences _preferences;
    private readonly TextBox _host = new() { Width = 220 };
    private readonly ListView _steps = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None, Dock = DockStyle.Fill, MultiSelect = false };
    private readonly Label _detail = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly Button _recheck = new() { Text = "Check again", AutoSize = true };
    private readonly Button _action = new() { Text = "", AutoSize = true, Visible = false };
    private readonly Button _close = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
    private SetupReport? _report;
    private bool _checking;

    public SetupForm(SetupCheck check, FramePreferences preferences)
    {
        _check = check; _preferences = preferences;
        Text = "SteamFrame Tray setup";
        Icon = TrayIcons.For(TrayState.Disarmed);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false; MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(560, 420);
        _host.Text = check.Host;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        top.Controls.AddRange([new Label { Text = "Frame address (name or IP):", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _host,
            new Label { Text = "“frame” over Wi-Fi, or 10.35.78.1 with the dongle", AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 6, 0, 0) }]);
        _steps.Columns.Add("Step", 210); _steps.Columns.Add("State", 90); _steps.Columns.Add("Result", 230);
        _steps.SelectedIndexChanged += (_, _) => ShowDetail();
        _detail.Padding = new Padding(8);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        bottom.Controls.AddRange([_close, _recheck, _action]);
        var middle = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 210, FixedPanel = FixedPanel.Panel1 };
        middle.Panel1.Controls.Add(_steps);
        middle.Panel2.Controls.Add(_detail);
        Controls.Add(middle); Controls.Add(top); Controls.Add(bottom);
        AcceptButton = _recheck; CancelButton = _close;
        _recheck.Click += async (_, _) => await RunAsync(null);
        _action.Click += async (_, _) => await ActAsync();
        Shown += async (_, _) => await RunAsync(null);
    }

    private async Task RunAsync(string? password)
    {
        if (_checking) return;
        _checking = true;
        _recheck.Enabled = _action.Enabled = false;
        _detail.Text = "Checking…";
        try
        {
            var host = _host.Text.Trim();
            if (!FrameAddress.IsValid(host)) { _detail.Text = "Enter the Frame's name (for example frame) or an IPv4 address."; return; }
            if (_preferences.SshHost != host) { _preferences.SshHost = host; }
            _report = await _check.RunAsync(password);
            Fill();
        }
        catch (Exception error) { _detail.Text = "The check could not run: " + error.Message; }
        finally { _checking = false; _recheck.Enabled = _action.Enabled = true; }
    }

    private void Fill()
    {
        _steps.Items.Clear();
        foreach (var step in _report!.Steps)
        {
            var label = step.State switch { StepState.Ok => "OK", StepState.NeedsAction => "Needs you", StepState.Failed => "Problem", _ => "Waiting" };
            _steps.Items.Add(new ListViewItem([step.Title, label, step.Detail.Length > 40 ? step.Detail[..39] + "…" : step.Detail]) { Tag = step });
        }
        var next = _report.Blocking;
        if (next is not null && _steps.Items.Count > 0) _steps.Items[_report.Steps.ToList().IndexOf(next)].Selected = true;
        _action.Visible = next is { Id: "hostkey" or "password" or "key" or "login" } && next.State is StepState.NeedsAction;
        _action.Text = next?.Id switch { "hostkey" => "Review host key…", "password" or "login" => "Enter Frame password…", "key" => "Choose key file…", _ => "" };
        if (next is null) _detail.Text = "Everything is ready. Close this window and arm H.264 from the tray menu.";
        else ShowDetail();
    }

    private void ShowDetail()
    {
        if (_steps.SelectedItems.Count == 1 && _steps.SelectedItems[0].Tag is SetupStep step) _detail.Text = step.Title + "\n\n" + step.Detail;
    }

    private async Task ActAsync()
    {
        var next = _report?.Blocking;
        if (next is null) return;
        switch (next.Id)
        {
            case "hostkey":
                var keys = _report!.PendingHostKeys;
                var lines = string.Join("\n", keys.Select(k => $"{k.KeyType}\n    {k.Fingerprint}"));
                var ask = MessageBox.Show(this, $"The Frame at “{keys[0].Host}” presented these host keys:\n\n{lines}\n\nThey can be compared with the Frame (Developer settings) if you want to be certain. Trust this Frame and remember its keys?\n\nFrom now on a different key is refused.",
                    "Trust this Frame?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (ask != DialogResult.Yes) return;
                try { await _check.PinHostKeysAsync(keys); }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Host key", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                await RunAsync(null);
                break;
            case "password":
            case "login":
                var password = PromptDialog.Ask("Frame password", "Enter the Frame user password (Frame settings → Developer → Set User Password). It is stored in Windows Credential Manager only after the Frame accepts it.", true);
                if (!string.IsNullOrEmpty(password)) await RunAsync(password);
                break;
            case "key":
                using (var dialog = new OpenFileDialog { Title = "Choose the SteamOS Devkit private key (devkit_rsa)", CheckFileExists = true })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    _preferences.SshKey = dialog.FileName;
                    await RunAsync(null);
                }
                break;
        }
    }
}
