namespace SteamFrame.Core;

public static partial class OpenVrSettings
{
    // A live benchmark needs only its five owned controls. Reading the entire
    // advanced-settings catalog every second adds unrelated runtime IPC work.
    internal static (double Target, bool Automatic, int StreamWidth, bool AutomaticWidth, bool AsyncSend)
        ReadBenchmarkControls(SteamPaths paths) => Session(paths, table =>
    {
        int error = 0;
        var target = Function<GetFloat>(table, 7)(Section, "targetBandwidth", ref error); Check(error, "Read target");
        var automatic = Function<GetBool>(table, 5)(Section, "automaticBandwidth", ref error); Check(error, "Read automatic bandwidth");
        var width = Function<GetInt>(table, 6)(Section, "streamFormatWidth", ref error); Check(error, "Read stream width");
        var automaticWidth = Function<GetBool>(table, 5)(Section, "automaticStreamFormatWidth", ref error); Check(error, "Read automatic stream width");
        var asyncSend = Function<GetBool>(table, 5)(Section, "asyncSend", ref error); Check(error, "Read sender request");
        return ((double)target, automatic, width, automaticWidth, asyncSend);
    });

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
