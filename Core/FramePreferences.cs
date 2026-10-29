using System.Text.Json;

namespace SteamFrame.Core;

/// <summary>One finished sweep, kept in preferences so the card can show how the link varies between runs.</summary>
public sealed record SweepRecord(DateTimeOffset At, int CeilingMbps, int RefreshHz, string Summary, bool NoCleanStep = false, bool LightlyTested = false, bool Confirmed = true)
{
    /// <summary>A clean ceiling the encoder never filled; records written before the flag existed carry it in their text.</summary>
    public bool IsFullyTested => !NoCleanStep && !LightlyTested && Confirmed && !Summary.Contains("lightly tested", StringComparison.Ordinal);
}

public sealed class FramePreferences
{
    public string AdbPath { get; set; } = DiscoverAdb();
    public string AdbSerial { get; set; } = "";
    public string SshHost { get; set; } = "";
    public string SshKey { get; set; } = DiscoverDevkitKey();
    public string SshKnownHosts { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamFrameTray", "ssh-known-hosts");
    public int GazeOffset { get; set; } = -4;
    public int PeripheralOffset { get; set; } = 8;
    public bool QualityEnabled { get; set; }
    public bool ReapplyAfterRestart { get; set; }
    public EncoderRateControl RateControl { get; set; }
    public EncoderMultiPass MultiPass { get; set; }
    public AdvancedEncoding AdvancedEncoding { get; set; } = new();
    public int CodecTargetMbps { get; set; } = 100;
    /// <summary>Experiment: the hook's rate control mode, "vbr" (the default) or "cbr". Takes effect the next time H.264 is armed.</summary>
    public string HookRateControl { get; set; } = "vbr";
    /// <summary>Experiment: the hook's NVENC preset, 1 (P1) to 7 (P7); the default is P2.</summary>
    public int HookPreset { get; set; } = 2;
    /// <summary>Experiment: the hook's VBV buffer in frames of the average bitrate, 1 (the default) to 16.</summary>
    public int HookVbvFrames { get; set; } = 1;
    /// <summary>Keep H.264 applied across SteamVR restarts; the Frame password is then kept in Windows Credential Manager.</summary>
    public bool KeepH264 { get; set; }
    public int BitrateSetupVersion { get; set; }
    /// <summary>Whether a finished sweep puts its measured ceiling on the stream straight away.</summary>
    public bool SweepApplyWhenDone { get; set; } = true;
    /// <summary>The last capacity sweep's highest clean target, kept so the measured ceiling survives restarts.</summary>
    public int? LastSweepCeilingMbps { get; set; }
    public DateTimeOffset? LastSweepAt { get; set; }
    public string LastSweepSummary { get; set; } = "";
    public int LastSweepRefreshHz { get; set; }
    /// <summary>Finished sweeps, newest last, so the card can show how much the link varies between runs.</summary>
    public List<SweepRecord> SweepHistory { get; set; } = [];
    /// <summary>Preferences written before the history existed carry one result; it becomes the first record.</summary>
    public void SeedSweepHistory()
    {
        if (SweepHistory.Count == 0 && LastSweepCeilingMbps is int ceiling && LastSweepAt is { } at)
            SweepHistory.Add(new SweepRecord(at, ceiling, LastSweepRefreshHz, LastSweepSummary));
    }
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamFrameTray", "frame.json");
    public static FramePreferences Load(string? path = null)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<FramePreferences>(File.ReadAllText(path ?? FilePath)) ?? new();
            loaded.SweepHistory ??= [];
            loaded.SeedSweepHistory();
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public async Task SaveAsync(string? path = null)
    {
        var destination = Path.GetFullPath(path ?? FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>Valve's SteamOS Devkit Client keeps its key in one of these folders, depending on its version.</summary>
    public static IEnumerable<string> DevkitKeyCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "steamos-devkit", "steamos-devkit", "devkit_rsa");
        yield return Path.Combine(local, "steamos-devkit", "devkit_rsa");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "devkit_rsa");
    }
    public static string DiscoverDevkitKey() => DevkitKeyCandidates().FirstOrDefault(File.Exists) ?? DevkitKeyCandidates().Skip(1).First();
    private static string DiscoverAdb()
    {
        var candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe");
        if (File.Exists(candidate)) return candidate;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (File.Exists(Path.Combine(folder, "adb.exe"))) return Path.Combine(folder, "adb.exe");
        return "";
    }
}
