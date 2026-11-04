namespace SteamFrame.Tests;

public class PatcherTests
{
    [Fact]
    public async Task CurrentDriverIsNotReportedPatchedUntilExplicitInstallation()
    {
        var fixture = Environment.GetEnvironmentVariable("SLVR_PATCHER_CURRENT_FIXTURE");
        if (string.IsNullOrWhiteSpace(fixture) || !File.Exists(fixture)) Assert.Skip("Set SLVR_PATCHER_CURRENT_FIXTURE to test the current driver on isolated copies.");
        var original = await File.ReadAllBytesAsync(fixture, TestContext.Current.CancellationToken);
        Assert.Equal(SteamFrame.Core.BitratePatcher.SupportedCurrentOriginalSha256,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)).ToLowerInvariant());
        var directory = Path.Combine(Path.GetTempPath(), "frame-patch-onboarding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "driver_vrlink.dll"); await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
            var patcher = new SteamFrame.Core.BitratePatcher(SteamFrame.Core.PatchManifest.LoadSupported(), () => false);
            var before = await patcher.InspectAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(SteamFrame.Core.BitratePatchState.Original, before.State); Assert.False(before.IsPatched); Assert.True(before.CanApply);
            var after = await patcher.ApplyAsync(path, TestContext.Current.CancellationToken);
            Assert.True(after.IsPatched); Assert.Equal(SteamFrame.Core.BitrateBackupState.VerifiedOriginal, after.BackupState);
            Assert.Equal(SteamFrame.Core.BitratePatcher.SupportedCurrentPatchedSha256, after.Sha256);
            Assert.Equal(original, await File.ReadAllBytesAsync(after.BackupPath, TestContext.Current.CancellationToken));
            var restored = await patcher.RestoreAsync(path, TestContext.Current.CancellationToken);
            Assert.False(restored.IsPatched); Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    /// <summary>
    /// Needs a verified original driver, which is never committed. Set SLVR_PATCHER_FIXTURE to the legacy
    /// original and optionally SLVR_PATCHER_CURRENT_FIXTURE to the current original.
    /// </summary>
    [Fact]
    public async Task PatchRestoreAndRefusalPaths()
    {
        var legacy = Environment.GetEnvironmentVariable("SLVR_PATCHER_FIXTURE");
        if (string.IsNullOrWhiteSpace(legacy) || !File.Exists(legacy))
            Assert.Skip("Set SLVR_PATCHER_FIXTURE to a verified original driver_vrlink.dll to run patcher tests.");
        var current = Environment.GetEnvironmentVariable("SLVR_PATCHER_CURRENT_FIXTURE");
        await PatcherHarness.RunAsync(string.IsNullOrWhiteSpace(current) ? [legacy] : [legacy, current]);
    }
}
