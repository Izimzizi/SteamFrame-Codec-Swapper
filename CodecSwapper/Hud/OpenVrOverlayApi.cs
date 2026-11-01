using System.Runtime.InteropServices;
using System.Text;

namespace CodecSwapper;

/// <summary>
/// SteamVR's overlay API through the openvr_api.dll that ships with SteamVR (nothing is bundled). The interfaces are reached through their
/// vtables; the slot numbers are the declaration order in openvr.h for IVROverlay_028 and IVRSystem_026, and connecting checks them by
/// asking the overlay interface for a known error name before anything else is called.
/// </summary>
public sealed unsafe class OpenVrOverlayApi(string steamVrRoot, Action<string>? log = null) : IOverlayApi
{
    private const string OverlayVersion = "IVROverlay_028", SystemVersion = "IVRSystem_026";
    private const int ApplicationOverlay = 2, StandingUniverse = 1, MouseInput = 1, MaxDevices = 64, EventSize = 64;
    private const uint MouseMove = 300, MouseDown = 301, MouseUp = 302, FocusEnter = 303, FocusLeave = 304;
    // Slot numbers: position among the virtual methods of the interface class in openvr.h.
    private const int CreateOverlaySlot = 1, DestroyOverlaySlot = 3, ErrorNameSlot = 8, SetAlphaSlot = 16, SetWidthSlot = 22, SetTrackedDeviceRelativeSlot = 35,
        SetTextureSlot = 60, ShowSlot = 43, HideSlot = 44, PollOverlayEventSlot = 48, SetInputMethodSlot = 50, SetMouseScaleSlot = 52, SetRawSlot = 62, IsDashboardVisibleSlot = 68;
    private const int GetPosesSlot = 12, ControllerIndexSlot = 18;

    private delegate* unmanaged<int*, int, uint> _init;
    private delegate* unmanaged<void> _shutdown;
    private delegate* unmanaged<byte*, int*, nint> _getInterface;
    private nint _module, _overlay, _system;

    public bool Connected => _overlay != 0;

    public bool TryConnect(out string error)
    {
        error = "";
        if (Connected) return true;
        // Initialising as an overlay application can start SteamVR; this tray must only ever join one that is already running.
        if (!VrServerRunning()) { error = "SteamVR is not running."; return false; }
        try
        {
            if (_module == 0)
            {
                var path = Path.Combine(steamVrRoot, "bin", "win64", "openvr_api.dll");
                if (!NativeLibrary.TryLoad(path, out _module)) { error = "openvr_api.dll was not found in SteamVR."; return false; }
                _init = (delegate* unmanaged<int*, int, uint>)NativeLibrary.GetExport(_module, "VR_InitInternal");
                _shutdown = (delegate* unmanaged<void>)NativeLibrary.GetExport(_module, "VR_ShutdownInternal");
                _getInterface = (delegate* unmanaged<byte*, int*, nint>)NativeLibrary.GetExport(_module, "VR_GetGenericInterface");
            }
            int code = 0;
            _init(&code, ApplicationOverlay);
            if (code != 0) { error = $"SteamVR is not accepting overlays yet (error {code})."; return false; }
            _overlay = Interface(OverlayVersion, out code);
            _system = Interface(SystemVersion, out var systemCode);
            if (_overlay == 0 || _system == 0 || code != 0 || systemCode != 0) { Disconnect(); error = $"This SteamVR does not provide {OverlayVersion}/{SystemVersion} ({code}/{systemCode})."; return false; }
            var name = Marshal.PtrToStringAnsi(((delegate* unmanaged<nint, int, nint>)Slot(_overlay, ErrorNameSlot))(_overlay, 0));
            if (name != "VROverlayError_None") { Disconnect(); error = "The overlay interface layout is not the one this tray was built for."; return false; }
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or ArgumentException or InvalidOperationException)
        {
            Disconnect();
            error = exception.Message;
            return false;
        }
    }

    public static bool IsVrServerRunning() => VrServerRunning();

    private static bool VrServerRunning()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("vrserver");
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    public void Disconnect()
    {
        if (_overlay == 0 && _system == 0) return;
        _overlay = _system = 0;
        _gpu?.Dispose(); _gpu = null;
        try { _shutdown(); } catch (Exception) { /* already gone with SteamVR */ }
    }

    // SteamVR's own quit event is not polled: the process check is enough and avoids another native call into the runtime.
    public bool StillRunning()
    {
        if (!Connected) return false;
        if (Environment.TickCount64 - _checkedAt >= 1000) { _checkedAt = Environment.TickCount64; _running = VrServerRunning(); }
        return _running;
    }

    private long _checkedAt;
    private bool _running = true;

    public ulong CreateOverlay(string key, string name)
    {
        ulong handle = 0;
        var keyBytes = Encoding.UTF8.GetBytes(key + "\0");
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* k = keyBytes)
        fixed (byte* n = nameBytes)
        {
            var result = ((delegate* unmanaged<nint, byte*, byte*, ulong*, int>)Slot(_overlay, CreateOverlaySlot))(_overlay, k, n, &handle);
            if (result == 0) return handle;
            // A previous run of this tray left the overlay behind: reuse it.
            if (((delegate* unmanaged<nint, byte*, ulong*, int>)Slot(_overlay, 0))(_overlay, k, &handle) == 0) return handle;
        }
        log?.Invoke($"CreateOverlay failed ({Name(_overlay, 0)})");
        return 0;
    }

    public void DestroyOverlay(ulong overlay) => ((delegate* unmanaged<nint, ulong, int>)Slot(_overlay, DestroyOverlaySlot))(_overlay, overlay);
    public void SetWidth(ulong overlay, float meters) => ((delegate* unmanaged<nint, ulong, float, int>)Slot(_overlay, SetWidthSlot))(_overlay, overlay, meters);
    public void SetAlpha(ulong overlay, float alpha) => ((delegate* unmanaged<nint, ulong, float, int>)Slot(_overlay, SetAlphaSlot))(_overlay, overlay, alpha);

    public void EnableLaser(ulong overlay, int panelWidth, int panelHeight)
    {
        ((delegate* unmanaged<nint, ulong, int, int>)Slot(_overlay, SetInputMethodSlot))(_overlay, overlay, MouseInput);
        var scale = stackalloc float[2] { panelWidth, (float)panelHeight };
        ((delegate* unmanaged<nint, ulong, float*, int>)Slot(_overlay, SetMouseScaleSlot))(_overlay, overlay, scale);
    }

    public void SetImage(ulong overlay, byte[] rgba, int width, int height)
    {
        if (!_gpuFailed && TrySetTexture(overlay, rgba, width, height)) return;
        fixed (byte* pixels = rgba)
        {
            var result = ((delegate* unmanaged<nint, ulong, byte*, uint, uint, uint, int>)Slot(_overlay, SetRawSlot))(_overlay, overlay, pixels, (uint)width, (uint)height, 4);
            if (result != 0) log?.Invoke($"SetOverlayRaw failed ({Name(_overlay, result)})");
        }
    }

    // One Direct3D texture, rewritten in place: SteamVR keeps showing it while it changes, so the panel does not flicker.
    private bool TrySetTexture(ulong overlay, byte[] rgba, int width, int height)
    {
        try
        {
            _gpu ??= GpuOverlayTexture.TryCreate(width, height);
            if (_gpu is null) { _gpuFailed = true; log?.Invoke("HUD: no Direct3D 11 device; using the slower image upload."); return false; }
            _gpu.Update(rgba);
            var texture = stackalloc nint[2];
            texture[0] = _gpu.Texture;
            texture[1] = 0; // ETextureType DirectX (0) and EColorSpace Auto (0)
            var result = ((delegate* unmanaged<nint, ulong, nint*, int>)Slot(_overlay, SetTextureSlot))(_overlay, overlay, texture);
            if (result == 0) return true;
            log?.Invoke($"SetOverlayTexture failed ({Name(_overlay, result)}); using the slower image upload.");
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { log?.Invoke("HUD: texture path unavailable: " + error.Message); }
        _gpuFailed = true;
        _gpu?.Dispose(); _gpu = null;
        return false;
    }

    private GpuOverlayTexture? _gpu;
    private bool _gpuFailed;

    public void SetVisible(ulong overlay, bool visible) =>
        ((delegate* unmanaged<nint, ulong, int>)Slot(_overlay, visible ? ShowSlot : HideSlot))(_overlay, overlay);

    public uint ControllerIndex(bool left) =>
        ((delegate* unmanaged<nint, int, uint>)Slot(_system, ControllerIndexSlot))(_system, left ? 1 : 2);

    public void AttachToDevice(ulong overlay, uint device, Pose relative)
    {
        var matrix = relative.ToMatrix34();
        fixed (float* m = matrix)
        {
            var result = ((delegate* unmanaged<nint, ulong, uint, float*, int>)Slot(_overlay, SetTrackedDeviceRelativeSlot))(_overlay, overlay, device, m);
            if (result != 0) log?.Invoke($"SetOverlayTransformTrackedDeviceRelative failed ({Name(_overlay, result)})");
        }
    }

    public bool TryDevicePose(uint device, out Pose pose)
    {
        pose = Pose.Identity;
        if (device >= MaxDevices) return false;
        var poses = stackalloc byte[MaxDevices * 80];
        ((delegate* unmanaged<nint, int, float, byte*, uint, void>)Slot(_system, GetPosesSlot))(_system, StandingUniverse, 0f, poses, MaxDevices);
        var entry = poses + device * 80;
        // TrackedDevicePose_t: 3x4 matrix at 0, tracking result at 72, validity at 76.
        if (entry[76] == 0 || *(int*)(entry + 72) != 200) return false;
        var matrix = new float[12];
        for (var i = 0; i < 12; i++) matrix[i] = ((float*)entry)[i];
        return Pose.TryFromMatrix34(matrix, out pose);
    }

    public bool DashboardVisible() => ((delegate* unmanaged<nint, byte>)Slot(_overlay, IsDashboardVisibleSlot))(_overlay) != 0;

    public IReadOnlyList<OverlayEvent> PollEvents(ulong overlay)
    {
        var events = new List<OverlayEvent>();
        // Generously sized: the runtime fills what it is told, but never trust a native writer with an exact fit.
        var buffer = stackalloc byte[1024];
        while (events.Count < 64 && ((delegate* unmanaged<nint, ulong, byte*, uint, byte>)Slot(_overlay, PollOverlayEventSlot))(_overlay, overlay, buffer, EventSize) != 0)
        {
            var type = *(uint*)buffer;
            var kind = type switch { MouseMove => OverlayEventKind.MouseMove, MouseDown => OverlayEventKind.MouseDown, MouseUp => OverlayEventKind.MouseUp, FocusEnter => OverlayEventKind.FocusEnter, FocusLeave => OverlayEventKind.FocusLeave, _ => OverlayEventKind.Other };
            events.Add(new(kind, *(uint*)(buffer + 4), *(float*)(buffer + 16), *(float*)(buffer + 20)));
        }
        return events;
    }

    public void Dispose() => Disconnect();

    private nint Interface(string version, out int error)
    {
        error = 0;
        var bytes = Encoding.ASCII.GetBytes(version + "\0");
        int code;
        nint result;
        fixed (byte* v = bytes) result = _getInterface(v, &code);
        error = code;
        return result;
    }

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private string Name(nint overlay, int error) =>
        Marshal.PtrToStringAnsi(((delegate* unmanaged<nint, int, nint>)Slot(overlay, ErrorNameSlot))(overlay, error)) ?? error.ToString();
}
