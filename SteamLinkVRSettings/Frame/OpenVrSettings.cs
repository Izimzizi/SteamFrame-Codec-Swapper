using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamLinkVRSettings.Frame;

// Uses Valve's installed loader; no OpenVR binary is redistributed.
public static partial class OpenVrSettings
{
    private static readonly object Gate = new();
    private const string Section = "driver_vrlink";
    internal const int TemporaryPowerTimeout = 3600;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Init(ref int error, int appType, nint startupInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Interface([MarshalAs(UnmanagedType.LPStr)] string version, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetBool([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.I1)] bool value, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetInt([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, int value, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetFloat([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, float value, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetString([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPStr)] string value, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool GetBool([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate float GetFloat([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInt([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void RemoveKey([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetString([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, StringBuilder value, uint capacity, ref int error);

    public static bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName("vrserver");
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
    public static async Task WaitUntilAvailableAsync(SteamPaths paths)
    {
        // Successful codec rollback precedes the replacement vrserver's settings
        // connection. Retry that connection before applying the retained controls.
        var timer = Stopwatch.StartNew();
        Exception? last = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            try { await Task.Run(() => Read(paths)); return; }
            catch (IOException error) { last = error; }
            await Task.Delay(300);
        }
        throw new IOException("HEVC components were restored, but SteamVR is still waiting for the Frame. Reconnect it and apply again; the requested controls remain in the editor.", last);
    }
    public static void Apply(SteamPaths paths, IReadOnlyDictionary<string, object?> values) => Session(paths, table =>
    {
        foreach (var pair in values)
        {
            int error = 0;
            switch (pair.Value)
            {
                case bool b: Function<SetBool>(table, 1)(Section, pair.Key, b, ref error); break;
                case int i: Function<SetInt>(table, 2)(Section, pair.Key, i, ref error); break;
                case double d: Function<SetFloat>(table, 3)(Section, pair.Key, (float)d, ref error); break;
                case float f: Function<SetFloat>(table, 3)(Section, pair.Key, f, ref error); break;
                case string s: Function<SetString>(table, 4)(Section, pair.Key, s, ref error); break;
                default: throw new ArgumentException($"Unsupported setting value for {pair.Key}");
            }
            Check(error, "Set " + pair.Key);
            if (pair.Value is bool expected)
            {
                var actual = Function<GetBool>(table, 5)(Section, pair.Key, ref error);
                Check(error, "Read " + pair.Key);
                if (actual != expected) throw new IOException($"SteamVR did not accept {pair.Key}.");
            }
            else if (pair.Value is double or float)
            {
                var number = Convert.ToDouble(pair.Value);
                var actual = Function<GetFloat>(table, 7)(Section, pair.Key, ref error);
                Check(error, "Read " + pair.Key);
                if (Math.Abs(number - actual) > 0.01) throw new IOException($"SteamVR did not accept {pair.Key}.");
            }
            else if (pair.Value is int integer)
            {
                var actual = Function<GetInt>(table, 6)(Section, pair.Key, ref error);
                Check(error, "Read " + pair.Key);
                if (actual != integer) throw new IOException($"SteamVR did not accept {pair.Key}.");
            }
            else if (pair.Value is string text)
            {
                var actual = new StringBuilder(2048);
                Function<GetString>(table, 8)(Section, pair.Key, actual, (uint)actual.Capacity, ref error);
                Check(error, "Read " + pair.Key);
                if (actual.ToString() != text) throw new IOException($"SteamVR did not accept {pair.Key}.");
            }
        }
        return true;
    });

    /// <summary>Removes keys from the driver section so SteamVR falls back to its defaults (IVRSettings::RemoveKeyInSection).</summary>
    public static void RemoveKeys(SteamPaths paths, IEnumerable<string> keys) => Session(paths, table =>
    {
        foreach (var key in keys)
        {
            int error = 0;
            Function<RemoveKey>(table, 10)(Section, key, ref error);
            if (error != 5) Check(error, "Remove " + key); // Missing already means Valve default/fallback.
        }
        return true;
    });

    public static (double Target, bool Automatic, bool MultipleLinks, int StreamWidth, bool AutomaticWidth) Read(SteamPaths paths) => Session(paths, table =>
    {
        int error = 0;
        var target = Function<GetFloat>(table, 7)(Section, "targetBandwidth", ref error); Check(error, "Read target");
        var automatic = Function<GetBool>(table, 5)(Section, "automaticBandwidth", ref error); Check(error, "Read automatic bandwidth");
        var multiple = Function<GetBool>(table, 5)(Section, "allowMultipleLinks", ref error); Check(error, "Read multiple links");
        var width = Function<GetInt>(table, 6)(Section, "streamFormatWidth", ref error); Check(error, "Read stream width");
        var automaticWidth = Function<GetBool>(table, 5)(Section, "automaticStreamFormatWidth", ref error); Check(error, "Read automatic stream width");
        return ((double)target, automatic, multiple, width, automaticWidth);
    });


    /// <summary>Explicitly asks Valve to reconfigure, preserving the latest target and every other stream setting.</summary>
    public static void RefreshEncoder(SteamPaths paths) => Session(paths, table =>
    {
        double ReadTarget()
        {
            int error = 0;
            var value = Function<GetFloat>(table, 7)(Section, "targetBandwidth", ref error);
            Check(error, "Read current target"); return value;
        }
        void WriteTarget(double value)
        {
            int error = 0;
            Function<SetFloat>(table, 3)(Section, "targetBandwidth", (float)value, ref error);
            Check(error, "Refresh encoder target");
        }
        RefreshTarget(ReadTarget, WriteTarget, () => Thread.Sleep(400));
        return true;
    });

    internal static void RefreshTarget(Func<double> read, Action<double> write, Action wait)
    {
        var original = read();
        if (!double.IsFinite(original) || original < 25 || original > 500)
            throw new InvalidOperationException("The live target is outside 25–500 Mbps. Reconnect to apply the encoder policy.");
        var temporary = original >= 500 ? original - 1 : original + 1;
        try { write(temporary); wait(); }
        finally
        {
            // A different app or the user may have changed the target during the
            // refresh. Restore only the value this operation actually set.
            if (Math.Abs(read() - temporary) < 0.01) write(original);
        }
    }

    internal static (bool Pause, int Timeout) ReadPower(SteamPaths paths) => Session(paths, table =>
    {
        int error = 0;
        var pause = Function<GetBool>(table, 5)("power", "pauseCompositorOnStandby", ref error); Check(error, "Read standby pause");
        var timeout = Function<GetInt>(table, 6)("power", "turnOffScreensTimeout", ref error); Check(error, "Read screen timeout");
        return (pause, timeout);
    });
    internal static void WritePower(SteamPaths paths, bool? pause, int? timeout) => Session(paths, table =>
    {
        int error = 0;
        if (pause is bool b) { Function<SetBool>(table, 1)("power", "pauseCompositorOnStandby", b, ref error); Check(error, "Set standby pause"); }
        if (timeout is int i) { Function<SetInt>(table, 2)("power", "turnOffScreensTimeout", i, ref error); Check(error, "Set screen timeout"); }
        return true;
    });
    internal static void RestorePower(SteamPaths paths, bool originalPause, int originalTimeout) => Session(paths, table =>
    {
        int error = 0;
        var pause = Function<GetBool>(table, 5)("power", "pauseCompositorOnStandby", ref error); Check(error, "Read standby pause before restore");
        if (!pause) { Function<SetBool>(table, 1)("power", "pauseCompositorOnStandby", originalPause, ref error); Check(error, "Restore standby pause"); }
        var timeout = Function<GetInt>(table, 6)("power", "turnOffScreensTimeout", ref error); Check(error, "Read screen timeout before restore");
        if (timeout == TemporaryPowerTimeout) { Function<SetInt>(table, 2)("power", "turnOffScreensTimeout", originalTimeout, ref error); Check(error, "Restore screen timeout"); }
        return true;
    });
    private static T Function<T>(nint table, int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, index * IntPtr.Size));
    private static void Check(int error, string operation) { if (error != 0) throw new IOException($"{operation} failed (OpenVR settings error {error})."); }
    private static T Session<T>(SteamPaths paths, Func<nint, T> action)
        => WithInterface(paths, "FnTable:IVRSettings_003", action);

    // OpenVR initialization is process-wide. All ephemeral interface users share
    // this gate so settings shutdown cannot invalidate another reader's pointer.
    internal static T WithInterface<T>(SteamPaths paths, string version, Func<nint, T> action)
    {
        lock (Gate)
        {
            if (!IsRunning) throw new InvalidOperationException("SteamVR is stopped. Settings can be saved for the next session.");
            var library = NativeLibrary.Load(paths.OpenVrApi);
            bool initialized = false;
            try
            {
                int error = 0;
                Marshal.GetDelegateForFunctionPointer<Init>(NativeLibrary.GetExport(library, "VR_InitInternal2"))(ref error, 3, 0);
                if (error != 0) throw new IOException($"Background OpenVR connection failed ({error}).");
                initialized = true;
                var table = Marshal.GetDelegateForFunctionPointer<Interface>(NativeLibrary.GetExport(library, "VR_GetGenericInterface"))(version, ref error);
                if (table == 0 || error != 0) throw new IOException(version == "FnTable:IVRSettings_003"
                    ? $"SteamVR settings interface unavailable ({error})." : $"SteamVR timing interface unavailable ({error}).");
                return action(table);
            }
            finally
            {
                if (initialized) Marshal.GetDelegateForFunctionPointer<Shutdown>(NativeLibrary.GetExport(library, "VR_ShutdownInternal"))();
                NativeLibrary.Free(library);
            }
        }
    }
}
