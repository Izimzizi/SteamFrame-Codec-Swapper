namespace SteamLinkVRSettings.Frame;

internal static class BitrateSetup
{
    public const int Version = 1;
    public static bool CanInstall(BitratePatchStatus? status) => status is { State: BitratePatchState.Original }
        && status.BackupState != BitrateBackupState.Mismatch;
    public static string Explanation(BitratePatchStatus? status) => status?.State switch
    {
        BitratePatchState.Original => "Original Valve driver verified. The stock ceiling is 232 Mbps when the dedicated wireless adapter is present, or 350 Mbps otherwise. Install the override to allow requests up to 500 Mbps. This does not guarantee that your connection can sustain that rate.",
        BitratePatchState.Patched => "Already installed: the driver file's SHA-256 and all patch locations match the 500 Mbps override. This is an existing change, not something this launch just installed. A previous app version or applying a bundled preset may have installed it. " +
            (status.BackupState == BitrateBackupState.VerifiedOriginal ? "A matching original backup is verified." : "The original backup is missing, so this app cannot restore it. SteamVR's Verify files can restore Valve's driver."),
        BitratePatchState.Missing => "SteamVR's streaming driver was not found. Install SteamVR or select the correct driver under Patches & Recovery, then check again. No patch is confirmed.",
        _ => "The installed driver could not be verified. No patch is confirmed and no driver changes will be made. Check Patches & Recovery for the file path and details. " + status?.Message
    };
}
