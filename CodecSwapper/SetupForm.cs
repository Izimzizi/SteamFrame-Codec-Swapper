using SteamFrame.Core;

namespace CodecSwapper;

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
    private readonly Button _close = new() { Text = "Close", AutoSize = true };
    private SetupReport? _report;
    private bool _checking;
    private bool _prompting;
    private readonly SplitContainer _middle = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };

    public SetupForm(SetupCheck check, FramePreferences preferences)
    {
        _check = check; _preferences = preferences;
        Text = "SteamFrame Codec Swapper setup";
        Icon = TrayIcons.For(TrayState.Disarmed);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 560);
        MinimumSize = new Size(520, 420);
        _host.Text = check.Host;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        top.Controls.AddRange([new Label { Text = "Frame address (name or IP):", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _host,
            new Label { Text = "“frame” over Wi-Fi, or 10.35.78.1 with the dongle", AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 6, 0, 0) }]);
        _steps.Columns.Add("Step", 210); _steps.Columns.Add("State", 90); _steps.Columns.Add("Result", 230);
        _steps.SelectedIndexChanged += (_, _) => ShowDetail();
        _detail.Padding = new Padding(8);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        bottom.Controls.AddRange([_close, _recheck, _action]);
        _middle.Panel1.Controls.Add(_steps);
        _middle.Panel2.Controls.Add(_detail);
        Controls.Add(_middle); Controls.Add(top); Controls.Add(bottom);
        Load += (_, _) => _middle.SplitterDistance = Math.Max(_middle.Panel1MinSize, _middle.Height - 130 * DeviceDpi / 96);
        _steps.Resize += (_, _) => FitColumns();
        AcceptButton = _recheck; CancelButton = _close;
        _close.Click += (_, _) => Close();
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
        FitSteps();
        var next = _report.Blocking;
        if (next is not null && _steps.Items.Count > 0) _steps.Items[_report.Steps.ToList().IndexOf(next)].Selected = true;
        _action.Visible = next is { Id: "hostkey" or "password" or "key" or "login" } && next.State is StepState.NeedsAction;
        _action.Text = next?.Id switch { "hostkey" => "Review host key…", "password" or "login" => "Enter Frame password…", "key" => "Choose key file…", _ => "" };
        if (next is null) _detail.Text = "Everything is ready. Close this window and choose H.264 from the tray menu.";
        else ShowDetail();
        // The step that stops everything opens its own dialog: setup cannot go on without it, so it should not wait for a button press.
        if (_action.Visible) BeginInvoke(async () => { Activate(); await ActAsync(); });
    }

    /// <summary>Every step is visible without scrolling: the window grows when the list needs more room than it has.</summary>
    private void FitSteps()
    {
        if (_steps.Items.Count == 0) return;
        var needed = _steps.Items.Count * _steps.GetItemRect(0).Height + _steps.Margin.Vertical + 8;
        var missing = needed - _middle.Panel1.Height;
        if (missing > 0)
        {
            var room = Screen.FromControl(this).WorkingArea.Height - Height;
            if (room > 0) Height += Math.Min(missing, room);
        }
        FitColumns();
    }

    private void FitColumns()
    {
        if (_steps.Columns.Count < 3) return;
        var rest = _steps.ClientSize.Width - _steps.Columns[0].Width - _steps.Columns[1].Width;
        if (rest > 80) _steps.Columns[2].Width = rest;
    }

    private void ShowDetail()
    {
        if (_steps.SelectedItems.Count == 1 && _steps.SelectedItems[0].Tag is SetupStep step) _detail.Text = step.Title + "\n\n" + step.Detail;
    }

    private async Task ActAsync()
    {
        var next = _report?.Blocking;
        if (next is null || _prompting || _checking) return;
        _prompting = true;
        try { await PromptAsync(next); }
        finally { _prompting = false; }
    }

    private async Task PromptAsync(SetupStep next)
    {
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
