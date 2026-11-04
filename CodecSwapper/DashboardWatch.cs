using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>
/// Catches the "dashboard came back without its desktop and Steam windows" case. Those windows are overlays that the SteamVR dashboard, the web helper
/// and Steam itself register with vrserver. After every codec switch (and every SteamVR start or stop) this writes what was running and which of them
/// had connected to vrserver, at +5, +15, +30 and +60 seconds, to <c>dashboard-watch.log</c>. When the windows are missing, the snapshot of that switch
/// shows which piece never came back, and the next switch that fixes it shows what differed.
/// </summary>
internal sealed class DashboardWatch
{
    private static readonly int[] Offsets = [5, 15, 30, 60];
    private static readonly string[] Wanted = ["vrdashboard.exe", "vrwebhelper.exe", "steam.exe", "steamwebhelper.exe", "vrcompositor.exe", "vrmonitor.exe"];
    private static readonly Regex Connect = new(@"^(?<time>.*?) \[Info\] - New Connect message from (?<path>.+?) \((?<kind>VRApplication_\w+)\)", RegexOptions.Compiled);
    private static readonly string[] Words = ["dashboard", "overlay", "desktop"];

    private readonly string _path;
    private readonly string _steamVrRoot;
    private readonly string _steamLogs;
    private readonly Func<string> _tray;
    private readonly Func<string, Task<string>>? _frame;
    private int _frameBusy;
    private readonly object _gate = new();
    private string _lastKey = "";
    private DateTime _changed = DateTime.MinValue;
    private int _next;
    private bool _wasRunning;

    public DashboardWatch(string path, string steamVrRoot, string steamRoot, Func<string> tray, Func<string, Task<string>>? frame = null)
    {
        _path = path; _steamVrRoot = steamVrRoot; _steamLogs = Path.Combine(steamRoot, "logs"); _tray = tray; _frame = frame;
    }

    /// <summary>Called every few seconds by the tray's poll. Cheap when nothing changed and no snapshot is due.</summary>
    public void Tick()
    {
        try
        {
            lock (_gate)
            {
                var running = OpenVrSettings.IsRunning;
                var key = _tray() + " | steamvr=" + running;
                var now = DateTime.Now;
                if (key != _lastKey)
                {
                    Write($"CHANGE {key}");
                    _lastKey = key; _changed = now; _next = 0;
                    Snapshot("at the change");
                }
                else if (running != _wasRunning) { _changed = now; _next = 0; }
                _wasRunning = running;
                if (_next < Offsets.Length && (now - _changed).TotalSeconds >= Offsets[_next])
                {
                    Snapshot($"+{Offsets[_next]} s");
                    _next++;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* diagnostics only */ }
    }

    private void Snapshot(string when)
    {
        var text = new StringBuilder($"SNAPSHOT {when}: ");
        text.Append(Processes()).Append(" || ").Append(Connected()).Append(" || ").Append(Dashboard());
        Write(text.ToString());
        if (_frame is not null && Interlocked.CompareExchange(ref _frameBusy, 1, 0) == 0) _ = FrameSnapshotAsync(when);
    }

    // What the Frame's own dashboard holds. The desktop and Steam windows reach it as frames that come from the PC over the stream (isVRLinkRemote):
    // after a switch with windows missing, these are the ones absent here. Reading it takes an SSH round trip, so it runs beside the other snapshot.
    internal const string FrameExpression = """
        (() => { try {
          const s = window.vrFrameStore, r = window.RemotePlayStore_SteamUI;
          const frames = s ? Array.from(s.m_mapFrames.values()).map(f => ({
            id: f.m_unFrameID, state: f.m_eState, visible: f.m_bVisible ? 1 : 0, remote: f.m_bIsVRLinkRemote ? 1 : 0, client: f.m_bIsStreamingClient ? 1 : 0,
            names: ((f.m_info && f.m_info.action_definitions) || []).map(a => a.display_name).join("|") })) : null;
          return JSON.stringify({ frames: frames, active: s && s.activeFrame ? s.activeFrame.m_unFrameID : null,
            streams: r ? r.remoteClientStreams.map(x => [String(x.m_gameid), x.m_unGroupID]) : null,
            overlays: window.overlayStore ? window.overlayStore.m_mapBrowserInfo.size : null });
        } catch (e) { return "error " + e; } })()
        """;

    private async Task FrameSnapshotAsync(string when)
    {
        string line;
        try
        {
            var reply = await _frame!(FrameExpression).ConfigureAwait(false);
            var value = System.Text.Json.JsonSerializer.Deserialize<string>(reply) ?? reply;
            line = $"FRAME {when}: {value}";
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException or FileNotFoundException or UnauthorizedAccessException or OperationCanceledException)
        {
            line = $"FRAME {when}: unreachable ({error.Message})";
        }
        finally { Interlocked.Exchange(ref _frameBusy, 0); }
        try { lock (_gate) Write(line); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* diagnostics only */ }
    }

    private string Processes()
    {
        var found = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName + ".exe";
                    if (!Wanted.Contains(name, StringComparer.OrdinalIgnoreCase) && !name.Equals("vrserver.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add($"{name}#{process.Id}@{process.StartTime:HH:mm:ss}");
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { found.Add(process.ProcessName + ".exe#" + process.Id); }
            }
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return "processes: " + (found.Count == 0 ? "none" : string.Join(' ', found));
    }

    /// <summary>The applications that connected to this vrserver session, newest connection last (read from vrserver.txt, which holds only the current session).</summary>
    private string Connected()
    {
        var file = Path.Combine(_steamLogs, "vrserver.txt");
        if (!File.Exists(file)) return "vrserver.txt: missing";
        var lines = new List<string>();
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            for (string? line; (line = reader.ReadLine()) is not null;)
                if (line.Contains("New Connect message from", StringComparison.Ordinal) || line.Contains("auto launch", StringComparison.OrdinalIgnoreCase)
                    || (line.Contains("ProcessDisconnected", StringComparison.Ordinal) && Words.Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase))))
                    lines.Add(line);
        }
        var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var match = Connect.Match(line);
            if (!match.Success) continue;
            var label = Path.GetFileName(match.Groups["path"].Value) + "(" + match.Groups["kind"].Value.Replace("VRApplication_", "") + ")";
            counts[label] = counts.GetValueOrDefault(label) + 1;
            latest[label] = match.Groups["time"].Value.Split(' ').LastOrDefault() ?? "";
        }
        var autoLaunch = lines.Where(l => l.Contains("auto launch", StringComparison.OrdinalIgnoreCase)).Select(l => l[(l.IndexOf("auto launch", StringComparison.OrdinalIgnoreCase))..]).Distinct().Take(6);
        var apps = counts.Select(c => $"{c.Key}x{c.Value}@{latest[c.Key]}");
        var started = File.GetCreationTime(file).ToString("HH:mm:ss");
        return $"vrserver.txt (created {started}) connected: {(counts.Count == 0 ? "nobody" : string.Join(' ', apps))}"
            + (autoLaunch.Any() ? " | " + string.Join(" ; ", autoLaunch) : "");
    }

    /// <summary>
    /// What the dashboard itself did in this SteamVR session: the window overlays its web UI created and destroyed (the desktop, the Steam window and any added
    /// window), how many dashboard requests it routed to the headset and the last reason, and the desktop view's own log lines.
    /// </summary>
    private string Dashboard()
    {
        var result = new StringBuilder("dashboard:");
        var ui = Path.Combine(_steamLogs, "vrwebhelper_systemui.txt");
        if (File.Exists(ui))
        {
            var lines = ReadLines(ui);
            var start = lines.FindLastIndex(l => l.Contains("startup with PID", StringComparison.Ordinal));
            var session = start >= 0 ? lines.Skip(start).ToList() : lines;
            var tally = new SortedDictionary<string, (int Created, int Destroyed)>(StringComparer.OrdinalIgnoreCase);
            var requests = 0; var lastReason = "";
            foreach (var line in session)
            {
                var created = line.Contains("[Overlays] Created: ", StringComparison.Ordinal);
                var destroyed = line.Contains("[Overlays] Destroyed: ", StringComparison.Ordinal);
                if (created || destroyed)
                {
                    var key = line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..].Trim();
                    if (key.StartsWith("system.window.", StringComparison.Ordinal)) key = "system.window.*";
                    if (key is "system.desktop.1" or "system.window.*" or "valve.steam.gamepadui.main" or "system.HeadsetView")
                    {
                        var (c, d) = tally.GetValueOrDefault(key);
                        tally[key] = created ? (c + 1, d) : (c, d + 1);
                    }
                }
                else if (line.Contains("[DashboardRequest] Send S->C", StringComparison.Ordinal))
                {
                    requests++;
                    var at = line.IndexOf("\"reason\":\"", StringComparison.Ordinal);
                    if (at >= 0) lastReason = line[(at + 10)..].Split('"')[0];
                }
            }
            result.Append(" overlays ").Append(tally.Count == 0 ? "none seen" : string.Join(' ', tally.Select(t => $"{t.Key}+{t.Value.Created}/-{t.Value.Destroyed}")));
            result.Append(" requests-to-headset ").Append(requests).Append(lastReason.Length > 0 ? " last " + lastReason : "");
        }
        else result.Append(" vrwebhelper_systemui.txt missing");
        var board = Path.Combine(_steamLogs, "vrdashboard.txt");
        if (File.Exists(board))
        {
            var lines = ReadLines(board);
            var start = lines.FindLastIndex(l => l.Contains("vrdashboard 2.", StringComparison.Ordinal) && l.Contains("startup", StringComparison.Ordinal));
            var interesting = lines.Skip(Math.Max(0, start)).Where(l => l.Contains("desktop view", StringComparison.OrdinalIgnoreCase) || l.Contains("duplication", StringComparison.OrdinalIgnoreCase)
                || l.Contains("Destroying overlay for window", StringComparison.Ordinal)).ToList();
            result.Append(" | vrdashboard: ").Append(interesting.Count).Append(" desktop/window events");
            if (interesting.Count > 0) result.Append(", last: ").Append(interesting[^1].Length > 140 ? interesting[^1][..140] : interesting[^1]);
        }
        return result.ToString();
    }

    private static List<string> ReadLines(string file)
    {
        var lines = new List<string>();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        for (string? line; (line = reader.ReadLine()) is not null;) lines.Add(line);
        return lines;
    }

    public void Note(string what) { try { lock (_gate) Write(what); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* diagnostics only */ } }

    private void Write(string what)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var info = new FileInfo(_path);
        if (info.Exists && info.Length > 2_000_000) File.Move(_path, _path + ".old", true);
        File.AppendAllText(_path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + what + Environment.NewLine);
    }
}
