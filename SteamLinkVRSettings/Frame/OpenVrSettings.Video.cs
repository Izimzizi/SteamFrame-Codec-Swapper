namespace SteamLinkVRSettings.Frame;

public static partial class OpenVrSettings
{
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
