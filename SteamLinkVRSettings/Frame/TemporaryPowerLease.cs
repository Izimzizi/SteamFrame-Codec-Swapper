using System.Text.Json;

namespace SteamLinkVRSettings.Frame;

internal interface IFramePowerSettings
{
    string Identity { get; }
    (bool Pause, int Timeout) Read();
    void KeepAwake();
    void Restore(bool originalPause, int originalTimeout);
}

internal sealed class OpenVrPowerSettings(SteamPaths paths) : IFramePowerSettings
{
    public string Identity => Path.GetFullPath(paths.SteamRoot);
    public (bool Pause, int Timeout) Read() => OpenVrSettings.ReadPower(paths);
    public void KeepAwake() => OpenVrSettings.WritePower(paths, false, TemporaryPowerLease.KeepAwakeSeconds);
    public void Restore(bool originalPause, int originalTimeout) => OpenVrSettings.RestorePower(paths, originalPause, originalTimeout);
}

/// <summary>Only restores an app-created lease; other diagnostic scripts' backups are never read.</summary>
internal sealed class TemporaryPowerLease(IFramePowerSettings settings, string? leasePath = null)
{
    public const int KeepAwakeSeconds = OpenVrSettings.TemporaryPowerTimeout;
    private readonly string _path = leasePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamLinkVRSettings", "temporary-power-lease.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record Lease(int Version, string SteamIdentity, bool OriginalPause, int OriginalTimeout);
    public bool Enabled { get; private set; }
    public bool RecoveryPending => File.Exists(_path);

    public async Task EnableAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Enabled) return;
            await RestoreCoreAsync(); // Recover only a previous app lease, if one exists.
            var original = await Task.Run(settings.Read);
            var lease = new Lease(1, settings.Identity, original.Pause, original.Timeout);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            // Persist before any SteamVR mutation so an interrupted enable is recoverable.
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(lease));
                File.Move(temporary, _path, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            try { await Task.Run(settings.KeepAwake); Enabled = true; }
            catch { await RestoreCoreAsync(); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreAsync()
    {
        await _gate.WaitAsync();
        try { await RestoreCoreAsync(); }
        finally { _gate.Release(); }
    }
    private async Task RestoreCoreAsync()
    {
        if (!File.Exists(_path)) { Enabled = false; return; }
        var lease = JsonSerializer.Deserialize<Lease>(await File.ReadAllTextAsync(_path))
            ?? throw new IOException("The temporary power lease could not be read; its recovery file was preserved.");
        if (lease.Version != 1 || !Path.GetFullPath(lease.SteamIdentity).Equals(Path.GetFullPath(settings.Identity), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The temporary power lease belongs to a different Steam installation; its recovery file was preserved.");
        // The backend compares each current value against our false/3600 override.
        // A user's newer, different value is retained. Failures keep the lease for retry.
        await Task.Run(() => settings.Restore(lease.OriginalPause, lease.OriginalTimeout));
        File.Delete(_path);
        Enabled = false;
    }
}
