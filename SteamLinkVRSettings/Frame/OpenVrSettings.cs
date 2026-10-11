using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamLinkVRSettings.Frame;

// Uses Valve's installed loader; no OpenVR binary is redistributed.
public static class OpenVrSettings
{
    private static readonly object Gate = new();
    private const string Section = "driver_vrlink";
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
            Check(error, "Remove " + key);
        }
        return true;
    });

    public static (double Target, bool Automatic, bool MultipleLinks) Read(SteamPaths paths) => Session(paths, table =>
    {
        int error = 0;
        var target = Function<GetFloat>(table, 7)(Section, "targetBandwidth", ref error); Check(error, "Read target");
        var automatic = Function<GetBool>(table, 5)(Section, "automaticBandwidth", ref error); Check(error, "Read automatic bandwidth");
        var multiple = Function<GetBool>(table, 5)(Section, "allowMultipleLinks", ref error); Check(error, "Read multiple links");
        return ((double)target, automatic, multiple);
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
