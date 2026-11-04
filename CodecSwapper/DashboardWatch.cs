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
    private readonly object _gate = new();
    private string _lastKey = "";
    private DateTime _changed = DateTime.MinValue;
    private int _next;
    private bool _wasRunning;
    private long _vrserverSeen = -1;

    public DashboardWatch(string path, string steamVrRoot, string steamRoot, Func<string> tray)
    {
        _path = path; _steamVrRoot = steamVrRoot; _steamLogs = Path.Combine(steamRoot, "logs"); _tray = tray;
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
        text.Append(Processes()).Append(" || ").Append(Connected());
        Write(text.ToString());
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

    private void Write(string what)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var info = new FileInfo(_path);
        if (info.Exists && info.Length > 2_000_000) File.Move(_path, _path + ".old", true);
        File.AppendAllText(_path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + what + Environment.NewLine);
    }
}
