using System.Runtime.InteropServices;
using System.Text;

namespace CodecSwapper;

/// <summary>
/// SteamVR's overlay API through the openvr_api.dll that ships with SteamVR (nothing is bundled). The interfaces are reached through their
/// vtables; the slot numbers are the declaration order in openvr.h for IVROverlay_028 and IVRSystem_026, and connecting checks them by
/// asking the overlay interface for a known error name before anything else is called.
/// </summary>
public sealed unsafe class OpenVrOverlayApi(string steamVrRoot, Action<string>? log = null, string? inputDirectory = null) : IOverlayApi
{
    private const string OverlayVersion = "IVROverlay_028", SystemVersion = "IVRSystem_026";
    private const int ApplicationOverlay = 2, StandingUniverse = 1, MouseInput = 1, MaxDevices = 64, EventSize = 64;
    private const uint MouseMove = 300, MouseDown = 301, MouseUp = 302, FocusEnter = 303, FocusLeave = 304;
    // Slot numbers: position among the virtual methods of the interface class in openvr.h.
    private const int CreateOverlaySlot = 1, DestroyOverlaySlot = 3, ErrorNameSlot = 8, SetAlphaSlot = 16, SetWidthSlot = 22, SetTrackedDeviceRelativeSlot = 35,
        SetTextureSlot = 60, ShowSlot = 43, HideSlot = 44, PollOverlayEventSlot = 48, SetInputMethodSlot = 50, SetMouseScaleSlot = 52, SetRawSlot = 62, IsDashboardVisibleSlot = 68;
    private const int GetPosesSlot = 12, ControllerIndexSlot = 18;
    private const int SetTransformAbsoluteSlot = 33, IsActiveDashboardOverlaySlot = 69, ShowDashboardSlot = 72;
    // IVRInput_010, in declaration order.
    private const string InputVersion = "IVRInput_010";
    private const int InputManifestSlot = 0, InputActionSetSlot = 1, InputActionSlot = 2, InputSourceSlot = 3, InputUpdateSlot = 4, InputDigitalSlot = 5, InputPoseSlot = 7;
    // An overlay application's action sets above this priority take their inputs from games too.
    private const int OverlayPriority = 0x01000000;
    private nint _input;
    private bool _inputReady, _fallbackLogged, _laserShown;
    private ulong _aimSet, _clickSet, _tipAction, _clickAction, _leftSource, _rightSource, _laserLine, _laserDot;
    private readonly HashSet<int> _inputErrors = [];

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
            InitInput();
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
        _overlay = _system = _input = 0;
        _inputReady = _laserShown = false; _laserLine = _laserDot = 0;
        foreach (var texture in _textures.Values) texture.Dispose();
        _textures.Clear();
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

    public void DestroyOverlay(ulong overlay)
    {
        ((delegate* unmanaged<nint, ulong, int>)Slot(_overlay, DestroyOverlaySlot))(_overlay, overlay);
        if (_textures.Remove(overlay, out var texture)) texture.Dispose();
    }
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
            // Each overlay has its own texture: the icon and the panel are different sizes.
            if (!_textures.TryGetValue(overlay, out var gpu))
            {
                gpu = GpuOverlayTexture.TryCreate(width, height);
                if (gpu is null) { _gpuFailed = true; log?.Invoke("HUD: no Direct3D 11 device; using the slower image upload."); return false; }
                _textures[overlay] = gpu;
            }
            gpu.Update(rgba);
            var texture = stackalloc nint[2];
            texture[0] = gpu.Texture;
            texture[1] = 0; // ETextureType DirectX (0) and EColorSpace Auto (0)
            var result = ((delegate* unmanaged<nint, ulong, nint*, int>)Slot(_overlay, SetTextureSlot))(_overlay, overlay, texture);
            if (result == 0) return true;
            log?.Invoke($"SetOverlayTexture failed ({Name(_overlay, result)}); using the slower image upload.");
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { log?.Invoke("HUD: texture path unavailable: " + error.Message); }
        _gpuFailed = true;
        foreach (var texture in _textures.Values) texture.Dispose();
        _textures.Clear();
        return false;
    }

    private readonly Dictionary<ulong, GpuOverlayTexture> _textures = [];
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

    private readonly HashSet<uint> _typesSeen = [];

    public IReadOnlyList<OverlayEvent> PollEvents(ulong overlay)
    {
        var events = new List<OverlayEvent>();
        // Generously sized: the runtime fills what it is told, but never trust a native writer with an exact fit.
        var buffer = stackalloc byte[1024];
        while (events.Count < 64 && ((delegate* unmanaged<nint, ulong, byte*, uint, byte>)Slot(_overlay, PollOverlayEventSlot))(_overlay, overlay, buffer, EventSize) != 0)
        {
            var type = *(uint*)buffer;
            if (type is not (MouseMove or MouseDown or MouseUp) && _typesSeen.Add(type)) log?.Invoke($"HUD: overlay event type {type} seen for the first time");
            var kind = type switch { MouseMove => OverlayEventKind.MouseMove, MouseDown => OverlayEventKind.MouseDown, MouseUp => OverlayEventKind.MouseUp, FocusEnter => OverlayEventKind.FocusEnter, FocusLeave => OverlayEventKind.FocusLeave, _ => OverlayEventKind.Other };
            events.Add(new(kind, *(uint*)(buffer + 4), *(float*)(buffer + 16), *(float*)(buffer + 20)));
        }
        return events;
    }

    public bool InputAvailable => _inputReady;

    private void InitInput()
    {
        _inputReady = false;
        if (inputDirectory is null) return;
        try
        {
            _input = Interface(InputVersion, out var code);
            if (_input == 0 || code != 0) { log?.Invoke($"HUD input: {InputVersion} is not available ({code}); no own laser."); _input = 0; return; }
            var manifest = HudInputManifest.Write(inputDirectory, "system.generated." + System.Diagnostics.Process.GetCurrentProcess().ProcessName.ToLowerInvariant() + ".exe");
            int error;
            var path = Encoding.UTF8.GetBytes(manifest + "\0");
            fixed (byte* p = path) error = ((delegate* unmanaged<nint, byte*, int>)Slot(_input, InputManifestSlot))(_input, p);
            if (error != 0) { log?.Invoke($"HUD input: SteamVR rejected the action manifest (error {error}); no own laser."); return; }
            _aimSet = InputHandle(InputActionSetSlot, HudInputManifest.AimSet, out var e1);
            _clickSet = InputHandle(InputActionSetSlot, HudInputManifest.ClickSet, out var e2);
            _tipAction = InputHandle(InputActionSlot, HudInputManifest.TipAction, out var e3);
            _clickAction = InputHandle(InputActionSlot, HudInputManifest.ClickAction, out var e4);
            _leftSource = InputHandle(InputSourceSlot, "/user/hand/left", out var e5);
            _rightSource = InputHandle(InputSourceSlot, "/user/hand/right", out var e6);
            if (e1 + e2 + e3 + e4 + e5 + e6 != 0 || _aimSet == 0 || _clickSet == 0 || _tipAction == 0 || _clickAction == 0 || _leftSource == 0 || _rightSource == 0)
            { log?.Invoke($"HUD input: handles not accepted ({e1}/{e2}/{e3}/{e4}/{e5}/{e6}); no own laser."); return; }
            _inputReady = true;
            log?.Invoke("HUD input: aim and trigger registered; the HUD draws its own laser while the dashboard is closed.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EntryPointNotFoundException or InvalidOperationException)
        {
            log?.Invoke("HUD input: could not start: " + exception.Message);
        }
    }

    private ulong InputHandle(int slot, string name, out int error)
    {
        ulong handle = 0;
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* n = bytes) error = ((delegate* unmanaged<nint, byte*, ulong*, int>)Slot(_input, slot))(_input, n, &handle);
        return handle;
    }

    private void InputError(string call, int error)
    {
        if (_inputErrors.Add(error)) log?.Invoke($"HUD input: {call} returned error {error}");
    }

    public bool TryAim(bool left, bool claimClick, out Pose tip, out bool pressed)
    {
        tip = Pose.Identity; pressed = false;
        if (!_inputReady) return false;
        // Two VRActiveActionSet_t (32 bytes each: set, restricted device, secondary set, padding, priority).
        var sets = stackalloc byte[64];
        new Span<byte>(sets, 64).Clear();
        *(ulong*)sets = _aimSet; *(int*)(sets + 28) = OverlayPriority;
        var count = 1u;
        if (claimClick) { *(ulong*)(sets + 32) = _clickSet; *(int*)(sets + 60) = OverlayPriority + 16; count = 2; }
        var error = ((delegate* unmanaged<nint, byte*, uint, uint, int>)Slot(_input, InputUpdateSlot))(_input, sets, 32, count);
        if (error != 0) { InputError("UpdateActionState", error); return false; }
        var source = left ? _leftSource : _rightSource;
        // InputPoseActionData_t: active flag, origin handle, then a TrackedDevicePose_t (matrix at 16, tracking result at 88, valid at 92).
        var pose = stackalloc byte[96];
        error = ((delegate* unmanaged<nint, ulong, int, float, byte*, uint, ulong, int>)Slot(_input, InputPoseSlot))(_input, _tipAction, StandingUniverse, 0f, pose, 96, source);
        var found = false;
        if (error == 0 && pose[0] != 0 && pose[92] != 0 && *(int*)(pose + 88) == 200)
        {
            var matrix = new float[12];
            for (var i = 0; i < 12; i++) matrix[i] = ((float*)(pose + 16))[i];
            found = Pose.TryFromMatrix34(matrix, out tip);
        }
        else if (error != 0) InputError("GetPoseActionData", error);
        if (!found)
        {
            // The binding has not loaded (yet) for this controller: aim along the controller itself.
            var device = ControllerIndex(left);
            if (device >= MaxDevices || !TryDevicePose(device, out tip)) return false;
            if (!_fallbackLogged) { _fallbackLogged = true; log?.Invoke("HUD input: no tip pose from the bindings; aiming along the controller body."); }
        }
        if (claimClick)
        {
            // InputDigitalActionData_t: active flag at 0, state at 16.
            var data = stackalloc byte[24];
            error = ((delegate* unmanaged<nint, ulong, byte*, uint, ulong, int>)Slot(_input, InputDigitalSlot))(_input, _clickAction, data, 24, source);
            if (error != 0) InputError("GetDigitalActionData", error);
            pressed = error == 0 && data[0] != 0 && data[16] != 0;
        }
        return true;
    }

    private const int LinePixelsWide = 1024, LinePixelsHigh = 6, DotPixels = 32;
    private const float DotWidth = .012f;

    public void ShowLaser(Pose line, float length, Pose dot)
    {
        if (!_inputReady) return;
        if (_laserLine == 0)
        {
            _laserLine = CreateOverlay("steamframe.tray.hud.laser", "SteamFrame HUD laser");
            _laserDot = CreateOverlay("steamframe.tray.hud.laser.dot", "SteamFrame HUD laser dot");
            if (_laserLine == 0 || _laserDot == 0) { _laserLine = _laserDot = 0; return; }
            SetAlpha(_laserLine, .9f); SetAlpha(_laserDot, 1f);
            SetWidth(_laserDot, DotWidth);
            var strip = new byte[LinePixelsWide * LinePixelsHigh * 4];
            for (var i = 0; i < strip.Length; i += 4) { strip[i] = 120; strip[i + 1] = 220; strip[i + 2] = 255; strip[i + 3] = 255; }
            SetRaw(_laserLine, strip, LinePixelsWide, LinePixelsHigh);
            var disc = new byte[DotPixels * DotPixels * 4];
            for (var y = 0; y < DotPixels; y++)
                for (var x = 0; x < DotPixels; x++)
                {
                    var d = MathF.Sqrt((x - 15.5f) * (x - 15.5f) + (y - 15.5f) * (y - 15.5f));
                    var o = (y * DotPixels + x) * 4;
                    if (d > 15.5f) continue;
                    var ring = d > 10.5f;
                    disc[o] = ring ? (byte)255 : (byte)120; disc[o + 1] = ring ? (byte)255 : (byte)220; disc[o + 2] = 255; disc[o + 3] = 255;
                }
            SetRaw(_laserDot, disc, DotPixels, DotPixels);
        }
        SetWidth(_laserLine, Math.Max(.01f, length));
        Place(_laserLine, line); Place(_laserDot, dot);
        if (!_laserShown) { SetVisible(_laserLine, true); SetVisible(_laserDot, true); _laserShown = true; }
    }

    public void HideLaser()
    {
        if (!_laserShown || !Connected) return;
        SetVisible(_laserLine, false); SetVisible(_laserDot, false);
        _laserShown = false;
    }

    private void Place(ulong overlay, Pose pose)
    {
        var matrix = pose.ToMatrix34();
        fixed (float* m = matrix) ((delegate* unmanaged<nint, ulong, int, float*, int>)Slot(_overlay, SetTransformAbsoluteSlot))(_overlay, overlay, StandingUniverse, m);
    }

    private void SetRaw(ulong overlay, byte[] rgba, int width, int height)
    {
        fixed (byte* pixels = rgba)
        {
            var result = ((delegate* unmanaged<nint, ulong, byte*, uint, uint, uint, int>)Slot(_overlay, SetRawSlot))(_overlay, overlay, pixels, (uint)width, (uint)height, 4);
            if (result != 0) log?.Invoke($"SetOverlayRaw failed ({Name(_overlay, result)})");
        }
    }

    /// <summary>
    /// Asks the dashboard to show the dashboard overlay that is already showing. That sends a dashboard request to the headset (the same kind adding a window to the
    /// dashboard sends), which makes the Frame read the dashboard's overlay list again after a codec switch; nothing visibly changes.
    /// </summary>
    public string RefreshDashboard()
    {
        if (!Connected) return "SteamVR not joined";
        var seen = new List<string>();
        foreach (var key in new[] { "system.desktop.1", "valve.steam.gamepadui.main" })
        {
            ulong handle = 0;
            var bytes = Encoding.UTF8.GetBytes(key + "\0");
            fixed (byte* k = bytes)
            {
                if (((delegate* unmanaged<nint, byte*, ulong*, int>)Slot(_overlay, 0))(_overlay, k, &handle) != 0 || handle == 0) { seen.Add(key + " missing"); continue; }
                var active = ((delegate* unmanaged<nint, ulong, byte>)Slot(_overlay, IsActiveDashboardOverlaySlot))(_overlay, handle) != 0;
                seen.Add(key + (active ? " active" : " not active"));
                if (!active) continue;
                ((delegate* unmanaged<nint, byte*, void>)Slot(_overlay, ShowDashboardSlot))(_overlay, k);
                return $"asked the dashboard to show {key} again ({string.Join(", ", seen)})";
            }
        }
        return "no known dashboard overlay is active, nothing sent (" + string.Join(", ", seen) + ")";
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
