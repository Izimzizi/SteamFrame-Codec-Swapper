using System.Text.Json;
using SteamLinkVRSettings.Frame;

public class SetupAndRefreshTests
{
    private static BitratePatchStatus Status(BitratePatchState state, BitrateBackupState backup = BitrateBackupState.Missing) => new("driver", "backup", state, backup, null, null, false, "detail");
    [Fact] public void NewPreferencesAlwaysOfferSetupWithoutAssumingPatch()
    {
        Assert.Equal(0, new FramePreferences().BitrateSetupVersion);
        Assert.True(BitrateSetup.CanInstall(Status(BitratePatchState.Original)));
        Assert.False(BitrateSetup.CanInstall(Status(BitratePatchState.Original, BitrateBackupState.Mismatch)));
        Assert.False(BitrateSetup.CanInstall(Status(BitratePatchState.Patched)));
        Assert.Contains("Original Valve driver verified", BitrateSetup.Explanation(Status(BitratePatchState.Original)));
        Assert.Contains("existing change", BitrateSetup.Explanation(Status(BitratePatchState.Patched)));
        Assert.Contains("No patch is confirmed", BitrateSetup.Explanation(Status(BitratePatchState.Unsupported)));
    }
    [Fact] public void PatchClassificationRequiresExactHashes()
    {
        var manifest = new PatchManifest { OriginalSha256 = "original", PatchedSha256 = "patched" };
        Assert.Equal(BitratePatchState.Original, BitratePatcher.ClassifyHash("original", manifest));
        Assert.Equal(BitratePatchState.Patched, BitratePatcher.ClassifyHash("patched", manifest));
        Assert.Throws<InvalidDataException>(() => BitratePatcher.ClassifyHash("unrecognized", manifest));
    }
    [Theory] [InlineData(90,90.00001)] [InlineData(120,119.99999)]
    public void RefreshConfirmsHardwareProperty(int requested, double observed)
    {
        var value = JsonSerializer.SerializeToElement(new { display_hz = observed, error = 0 });
        Assert.Equal(observed, FrameDisplayRate.Parse(value, requested));
        Assert.Contains("system[23]", FrameDisplayRate.Script(requested));
        Assert.Contains("read(0,2002", FrameDisplayRate.Script(requested));
    }
    [Theory] [InlineData(90,120,0)] [InlineData(120,90,0)] [InlineData(90,90,4)]
    public void SavedPreferenceWithoutActiveHardwareRateIsNotSuccess(int requested, int observed, int error)
    {
        Assert.Throws<IOException>(() => FrameDisplayRate.Parse(JsonSerializer.SerializeToElement(new { display_hz = observed, error }), requested));
    }
    [Fact] public void RefreshRejectsInvalidInputAndMissingReadback()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameDisplayRate.Script(144));
        Assert.False(FrameDisplayRate.Matches(double.NaN, 90));
        Assert.ThrowsAny<Exception>(() => FrameDisplayRate.Parse(JsonSerializer.SerializeToElement(new { preferred_hz = 120 }), 120));
    }
}
