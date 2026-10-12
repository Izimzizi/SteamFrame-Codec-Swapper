namespace SteamLinkVRSettings.Frame;

public static partial class OpenVrSettings
{
    public static Dictionary<string, DriverSettingReadback> ReadAdvanced(SteamPaths paths) => Session(paths, table =>
    {
        var result = new Dictionary<string, DriverSettingReadback>(StringComparer.Ordinal);
        foreach (var setting in AdvancedDriverSettings.Definitions)
        {
            int error = 0;
            object value = setting.Kind switch
            {
                DriverValueKind.Boolean => Function<GetBool>(table, 5)(Section, setting.Key, ref error),
                DriverValueKind.Integer => Function<GetInt>(table, 6)(Section, setting.Key, ref error),
                _ => (double)Function<GetFloat>(table, 7)(Section, setting.Key, ref error)
            };
            result[setting.Key] = error == 0 ? new(value, "OpenVR accepted setting; observe the stream to verify its effect.") :
                new(null, error == 5 ? "No exposed default; driver fallback applies." : $"OpenVR read unavailable ({error}).", error);
        }
        return result;
    });
}
