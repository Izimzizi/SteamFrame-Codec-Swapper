using System.Text.Json;

namespace SteamLinkVRSettings.Frame;

internal static class FrameDisplayRate
{
    internal sealed record State(double DisplayHz,double? PreferredHz);
    public static async Task<State> ReadAsync(FramePreferences preferences,CancellationToken token)
    {
        using var value=JsonDocument.Parse(await FrameSsh.ExecuteAsync(preferences,StateScript,token));
        var root=value.RootElement;
        return new(root.GetProperty("display_hz").GetDouble(),root.GetProperty("preferred_hz").ValueKind==JsonValueKind.Null?null:root.GetProperty("preferred_hz").GetDouble());
    }
    public static async Task RestorePreferenceAsync(FramePreferences preferences,double? value,CancellationToken token)
        => _=await FrameSsh.ExecuteAsync(preferences,StateScript.Replace("restore=False","restore=True").Replace("original=None",
            "original="+(value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None")),token);
    internal static string StateScript => """
import ctypes as c,json
restore=False
original=None
lib=c.CDLL('/opt/steamvr/bin/linuxarm64/libopenvr_api.so')
lib.VR_InitInternal2.argtypes=[c.POINTER(c.c_int),c.c_int,c.c_char_p];lib.VR_InitInternal2.restype=c.c_uint32
lib.VR_GetGenericInterface.argtypes=[c.c_char_p,c.POINTER(c.c_int)];lib.VR_GetGenericInterface.restype=c.c_void_p
error=c.c_int();lib.VR_InitInternal2(c.byref(error),3,None)
if error.value:raise RuntimeError('Frame display session unavailable')
def table(name):
 error.value=0;p=lib.VR_GetGenericInterface(name,c.byref(error))
 if not p or error.value:raise RuntimeError('Frame display interface unavailable')
 return c.cast(p,c.POINTER(c.c_void_p))
try:
 settings=table(b'FnTable:IVRSettings_003');system=table(b'FnTable:IVRSystem_026')
 get=c.CFUNCTYPE(c.c_float,c.c_char_p,c.c_char_p,c.POINTER(c.c_int))(settings[7])
 if restore:
  error.value=0
  if original is None:c.CFUNCTYPE(None,c.c_char_p,c.c_char_p,c.POINTER(c.c_int))(settings[10])(b'vrlink.client',b'preferredRefreshRate',c.byref(error))
  else:c.CFUNCTYPE(None,c.c_char_p,c.c_char_p,c.c_float,c.POINTER(c.c_int))(settings[3])(b'vrlink.client',b'preferredRefreshRate',original,c.byref(error))
  if error.value not in (0,5):raise RuntimeError('Frame display preference restore failed')
 error.value=0;preferred=get(b'vrlink.client',b'preferredRefreshRate',c.byref(error))
 if error.value==5:preferred=None
 elif error.value:raise RuntimeError('Frame display preference unavailable')
 error.value=0;rate=c.CFUNCTYPE(c.c_float,c.c_uint32,c.c_int,c.POINTER(c.c_int))(system[23])(0,2002,c.byref(error))
 if error.value:raise RuntimeError('Frame active display unavailable')
 print(json.dumps({'display_hz':rate,'preferred_hz':preferred}))
finally:lib.VR_ShutdownInternal()
""";
    internal static bool Matches(double actual, int requested) => double.IsFinite(actual) && Math.Abs(actual - requested) < .1;

    public static async Task<double> SetAsync(FramePreferences preferences, int hz, CancellationToken token)
    {
        using var value = JsonDocument.Parse(await FrameSsh.ExecuteAsync(preferences, Script(hz), token));
        return Parse(value.RootElement, hz);
    }

    internal static double Parse(JsonElement value, int hz)
    {
        var actual = value.GetProperty("display_hz").GetDouble();
        if (value.GetProperty("error").GetInt32() != 0 || !Matches(actual, hz))
            throw new IOException($"The Frame did not switch to {hz} Hz (reported {actual:0.##} Hz). Reconnect it and try again.");
        return actual;
    }

    internal static string Script(int hz)
    {
        if (hz is not (72 or 80 or 90 or 120)) throw new ArgumentOutOfRangeException(nameof(hz));
        // The active PC-streaming application owns a per-app refresh preference.
        // Its vrlink.client override takes precedence over the global steamvr key.
        return "fps=" + hz + "\n" + """
import ctypes as c,json,time
lib=c.CDLL('/opt/steamvr/bin/linuxarm64/libopenvr_api.so')
lib.VR_InitInternal2.argtypes=[c.POINTER(c.c_int),c.c_int,c.c_char_p];lib.VR_InitInternal2.restype=c.c_uint32
lib.VR_GetGenericInterface.argtypes=[c.c_char_p,c.POINTER(c.c_int)];lib.VR_GetGenericInterface.restype=c.c_void_p
error=c.c_int();lib.VR_InitInternal2(c.byref(error),3,None)
if error.value:raise RuntimeError('Frame display session unavailable')
def table(name):
 error.value=0;p=lib.VR_GetGenericInterface(name,c.byref(error))
 if not p or error.value:raise RuntimeError('Frame display interface unavailable')
 return c.cast(p,c.POINTER(c.c_void_p))
try:
 settings=table(b'FnTable:IVRSettings_003');system=table(b'FnTable:IVRSystem_026')
 write=c.CFUNCTYPE(None,c.c_char_p,c.c_char_p,c.c_float,c.POINTER(c.c_int))(settings[3])
 read=c.CFUNCTYPE(c.c_float,c.c_uint32,c.c_int,c.POINTER(c.c_int))(system[23])
 error.value=0;write(b'vrlink.client',b'preferredRefreshRate',fps,c.byref(error))
 if error.value:raise RuntimeError('Frame display preference rejected')
 rate=0
 for i in range(30):
  time.sleep(.1);error.value=0;rate=read(0,2002,c.byref(error))
  if not error.value and abs(rate-fps)<.1:break
 print(json.dumps({'display_hz':rate,'error':error.value}))
finally:lib.VR_ShutdownInternal()
""";
    }
}
