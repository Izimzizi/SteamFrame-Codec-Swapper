using SteamFrame.Core;

namespace SteamFrame.Tests;

public sealed class DriverDiagnosticsTests
{
    [Fact]
    public void VersionReportsInstalledBranchDuringPendingSwitch()
    {
        var report = DriverCompatibility.DescribeManifest(Manifest("25330290", "public", "beta"), "SteamVR");
        Assert.Contains("25330290 (public / stable)", report);
        Assert.DoesNotContain("(beta)", report);
    }

    [Theory]
    [InlineData("invalid", "SteamVR")]
    [InlineData("25839986", "AnotherRuntime")]
    public void InvalidOrUnrelatedManifestDoesNotClaimAnInstalledVersion(string build, string folder)
        => Assert.Contains("unavailable", DriverCompatibility.DescribeManifest(Manifest(build, "beta"), folder));

    [Fact]
    public async Task SupportedBuildNumberCannotAuthorizeUnknownBytesAndReportExplainsNextStep()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new DriverTestDirectory();
        var root = Path.Combine(fixture.Root, "steamapps", "common", "SteamVR");
        var path = Path.Combine(root, "drivers", "vrlink", "bin", "win64", "driver_vrlink.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "steamapps", "appmanifest_250820.acf"), Manifest("25839986", "beta"), token);
        var unknown = new byte[4096];
        await File.WriteAllBytesAsync(path, unknown, token);
        var patcher = new BitratePatcher(PatchManifest.LoadSupported(), () => false);
        var status = await patcher.InspectAsync(path, token);
        Assert.Contains("25839986 (beta)", status.InstalledVersion);
        Assert.Equal(BitratePatchState.Unsupported, status.State);
        Assert.False(status.CanApply);
        Assert.Contains("Update this app", status.Message);
        Assert.Contains("copy driver details", status.Message);
        Assert.Contains("Not a bounded PE image", status.Details);
        Assert.Contains("25330290", status.Details);
        Assert.Contains("25839986", status.Details);
        Assert.Contains(status.Message, status.PresetFailureMessage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => patcher.ApplyAsync(path, token));
        Assert.Equal(unknown, await File.ReadAllBytesAsync(path, token));
        Assert.False(File.Exists(status.BackupPath));
    }

    [Fact]
    public async Task SelectedCopyDoesNotBorrowTheLocalSteamInstallationVersion()
    {
        using var fixture = new DriverTestDirectory();
        var path = Path.Combine(fixture.Root, "driver_vrlink.dll");
        await File.WriteAllBytesAsync(path, new byte[4096], TestContext.Current.CancellationToken);
        Assert.Contains("unavailable", DriverCompatibility.InstalledVersion(path));
    }

    [Fact]
    public async Task MissingAndUnreadableDriverHaveDifferentRecoveryGuidance()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new DriverTestDirectory();
        var path = Path.Combine(fixture.Root, "driver_vrlink.dll");
        var patcher = new BitratePatcher(PatchManifest.LoadSupported(), () => false);
        Assert.Equal(BitratePatchState.Missing, (await patcher.InspectAsync(path, token)).State);
        await File.WriteAllBytesAsync(path, [1], token);
        var status = await patcher.InspectAsync(path, token);
        Assert.Equal(BitratePatchState.Unavailable, status.State);
        Assert.Contains("outside the supported bounds", status.Details);
        Assert.DoesNotContain("newer SteamVR", status.Message);
    }

    private static string Manifest(string build, string installed, string requested = "beta") => $$"""
        "AppState"
        {
            "appid" "250820"
            "installdir" "SteamVR"
            "buildid" "{{build}}"
            "UserConfig" { "BetaKey" "{{requested}}" }
            "MountedConfig" { "BetaKey" "{{installed}}" }
        }
        """;
}

internal sealed class DriverTestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "SteamLinkVRSettings-driver-tests-" + Guid.NewGuid().ToString("N"));
    public DriverTestDirectory() => Directory.CreateDirectory(Root);
    public void Dispose()
    {
        var prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "SteamLinkVRSettings-driver-tests-");
        if (Path.GetFullPath(Root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) Directory.Delete(Root, recursive: true);
    }
}
