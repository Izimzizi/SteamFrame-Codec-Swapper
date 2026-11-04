namespace CodecSwapper;

public enum OverlayEventKind { MouseMove, MouseDown, MouseUp, FocusEnter, FocusLeave, Other }

/// <summary>A laser event on the overlay: x and y are in the mouse scale the overlay was given (panel pixels), origin bottom-left; Device is the controller that produced it.</summary>
public readonly record struct OverlayEvent(OverlayEventKind Kind, uint Device, float X, float Y);

/// <summary>The few SteamVR overlay calls the HUD needs, so the HUD logic can be tested without SteamVR.</summary>
public interface IOverlayApi : IDisposable
{
    const uint NoDevice = uint.MaxValue;

    bool Connected { get; }
    /// <summary>Joins a running SteamVR as an overlay application. Never starts SteamVR.</summary>
    bool TryConnect(out string error);
    void Disconnect();
    /// <summary>False once SteamVR is shutting down.</summary>
    bool StillRunning();

    ulong CreateOverlay(string key, string name);
    void DestroyOverlay(ulong overlay);
    void SetWidth(ulong overlay, float meters);
    void SetAlpha(ulong overlay, float alpha);
    void EnableLaser(ulong overlay, int panelWidth, int panelHeight);
    void SetImage(ulong overlay, byte[] rgba, int width, int height);
    void SetVisible(ulong overlay, bool visible);
    uint ControllerIndex(bool left);
    void AttachToDevice(ulong overlay, uint device, Pose relative);
    /// <summary>The device's pose in the standing universe, false when it is not tracked right now.</summary>
    bool TryDevicePose(uint device, out Pose pose);
    IReadOnlyList<OverlayEvent> PollEvents(ulong overlay);
    /// <summary>Whether SteamVR's dashboard is open. SteamVR only passes laser input to overlays while it is, which is also when the HUD shows its controls.</summary>
    bool DashboardVisible();

    // Our own laser, for when the dashboard is closed and SteamVR draws none. Interfaces that cannot do it keep these defaults.
    /// <summary>Whether controller input (aim and trigger) can be read through SteamVR's input system.</summary>
    bool InputAvailable => false;
    /// <summary>
    /// The pointing hand's tip pose in the standing universe and whether its trigger is pressed. The trigger is only claimed from the game
    /// (and only reported) while <paramref name="claimClick"/> is true, so a game never loses its trigger unless the laser is on the HUD.
    /// </summary>
    bool TryAim(bool left, bool claimClick, out Pose tip, out bool pressed) { tip = Pose.Identity; pressed = false; return false; }
    /// <summary>Draws the laser: a line pose (Y along it, <paramref name="length"/> long) and a dot on the surface it hits.</summary>
    void ShowLaser(Pose line, float length, Pose dot) { }
    void HideLaser() { }
}
