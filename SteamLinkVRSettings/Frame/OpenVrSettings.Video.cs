namespace SteamLinkVRSettings.Frame;

public static partial class OpenVrSettings
{
    public static void RemovePreferredRefreshRate(SteamPaths paths) => Session(paths, table =>
    {
        int error = 0;
        Function<RemoveKey>(table, 10)(SteamVrVideoSection, "preferredRefreshRate", ref error);
        if (error != 5) Check(error, "Restore display refresh default");
        return true;
    });
    public static void SetPreferredRefreshRate(SteamPaths paths, double fps) => Session(paths, table =>
    {
        if (!double.IsFinite(fps) || fps < 0 || fps > 240) throw new ArgumentOutOfRangeException(nameof(fps));
        int error = 0;
        Function<SetFloat>(table, 3)(SteamVrVideoSection, "preferredRefreshRate", (float)fps, ref error);
        Check(error, "Set display refresh preference");
        var actual = Function<GetFloat>(table, 7)(SteamVrVideoSection, "preferredRefreshRate", ref error);
        Check(error, "Confirm display refresh preference");
        if (Math.Abs(actual - fps) > .01) throw new IOException("SteamVR did not accept the refresh preference.");
        return true;
    });
    // Valve's local openvr.h: k_pch_SteamVR_Section / k_pch_SteamVR_AllowSupersampleFiltering_Bool.
    public const string SteamVrVideoSection = "steamvr";
    public const string SupersampleFilteringKey = "allowSupersampleFiltering";

    public static bool ReadSupersampleFiltering(SteamPaths paths) => Session(paths, table =>
    {
        int error = 0;
        var enabled = Function<GetBool>(table, 5)(SteamVrVideoSection, SupersampleFilteringKey, ref error);
        Check(error, "Read Advanced Supersample Filtering");
        return enabled;
    });

    public static bool SetSupersampleFiltering(SteamPaths paths, bool enabled) => Session(paths, table =>
    {
        int error = 0;
        Function<SetBool>(table, 1)(SteamVrVideoSection, SupersampleFilteringKey, enabled, ref error);
        Check(error, "Set Advanced Supersample Filtering");
        var accepted = Function<GetBool>(table, 5)(SteamVrVideoSection, SupersampleFilteringKey, ref error);
        Check(error, "Confirm Advanced Supersample Filtering");
        if (accepted != enabled) throw new IOException("SteamVR did not accept Advanced Supersample Filtering.");
        return accepted;
    });
}
