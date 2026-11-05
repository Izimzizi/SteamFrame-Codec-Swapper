using System.Numerics;

namespace CodecSwapper;

/// <summary>What a press on the HUD does. The tray implements these with the same calls as its menu.</summary>
public interface IHudActions
{
    Task SetCodecAsync(bool h264);
    Task SetBitrateAsync(int mbps);
    /// <summary>Sets the stream width in pixels (SteamVR's streamFormatWidth).</summary>
    Task SetWidthAsync(int width);
    Task SetEncoderAsync(string rateControl, int preset, int vbvFrames);
    /// <summary>Sets the encoder's multipass mode (0 off, 1 quarter resolution first pass, 2 full resolution first pass).</summary>
    Task SetMultiPassAsync(int mode) => Task.CompletedTask;
    Task HideAsync();
    /// <summary>Starts the held-back / dropped / late frame totals again from zero.</summary>
    Task ResetCountersAsync();
}

/// <summary>
/// Keeps the HUD overlay in step with SteamVR: joins when SteamVR runs, hangs the panel on a controller, redraws it when its readouts change,
/// turns laser clicks into actions, and lets the panel be dragged to a new place on the controller.
/// </summary>
public sealed class HudController(IOverlayApi api, HudSettings settings, Func<HudView> readView, IHudActions actions, Action saveSettings, Func<bool> steamVrRunning, Action<string>? log = null) : IDisposable
{
    private const string OverlayKey = "steamframe.tray.hud", IconKey = "steamframe.tray.hud.icon", BubbleKey = "steamframe.tray.hud.guide";
    private static readonly TimeSpan ConnectRetry = TimeSpan.FromSeconds(3), DataUpload = TimeSpan.FromMilliseconds(100), InteractionUpload = TimeSpan.FromMilliseconds(25), SlideIdle = TimeSpan.FromMilliseconds(600), CommitHold = TimeSpan.FromSeconds(4);

    private ulong _overlay, _icon;
    private Pose _bubbleAt; private float _bubbleWidth; private uint _bubbleDevice = IOverlayApi.NoDevice;
    private uint _iconDevice = IOverlayApi.NoDevice;
    private string? _iconDrawn;
    private bool _iconHover, _panelVisible;
    private DateTime _iconUpload = DateTime.MinValue;
    private uint _attachedTo = IOverlayApi.NoDevice;
    private Pose _attachedPose;
    private float _attachedWidth;
    private HudView? _drawn;
    private string? _drawnHover, _hover;
    private DateTime _lastUpload = DateTime.MinValue, _nextConnect = DateTime.MinValue, _laserAt = DateTime.MinValue;
    private static readonly TimeSpan LaserHold = TimeSpan.FromSeconds(1), HoverHold = TimeSpan.FromMilliseconds(200);
    private bool _repositioning;
    private bool _leftByEvent;
    private long _expandedAt;                // Stopwatch ticks when the panel was told to expand, until its picture is uploaded
    private Pose _draftPose, _draftIconPose, _iconPose;
    private float _draftWidth, _draftIconWidth, _iconWidth;
    private bool _resizeIcon;              // Smaller/Larger act on the watch icon (the one grabbed last) instead of the panel
    private Grab? _grab;
    private string _note = "";
    private string _menu = "";
    private string _page = "";
    private bool _dashboard = true;
    private int _guideStep = -2;             // -2 not decided yet, -1 finished or skipped, 0 the bubble at the watch, 1.. the steps on the panel
    private ulong _bubble;                 // the overlay that points at the watch during step 0
    private string? _slidingSetting;       // "slide:angle" or "slide:dist" while a fade-out slider is held
    private int? _sliding;                 // the bitrate under the laser while the slider is held
    private int? _slidingWidth, _committedWidth;   // the same for the stream width slider
    private int? _committed;               // the bitrate just chosen, shown until the tray reports it
    private DateTime _committedAt, _lastSlideEvent, _clock;

    // The HUD's own laser: used while the dashboard is closed, when SteamVR draws none.
    private enum Over { None, Icon, Panel }
    private readonly List<OverlayEvent> _ownIcon = [], _ownPanel = [];
    private Over _over = Over.None;
    private bool _ownDown;
    private DateTime _nativeAt = DateTime.MinValue;
    private static readonly TimeSpan NativeLaserHold = TimeSpan.FromMilliseconds(1500);

    private sealed record Grab(uint Device, Pose DeviceAtGrab, Pose PanelAtGrab, bool Icon);

    public bool Repositioning => _repositioning;
    public bool Attached => _overlay != 0;
    public string OpenMenu => _menu;
    public string Page => _page;

    /// <summary>Call about 30 times a second from the UI thread.</summary>
    public void Tick(DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        _clock = at;
        if (!settings.Enabled || !steamVrRunning()) { Teardown(); return; }
        if (!settings.Received) return;   // the tray's settings (first-run guide done, placement) have not arrived yet
        if (!api.Connected)
        {
            if (at < _nextConnect) return;
            if (!api.TryConnect(out var error)) { _nextConnect = at + ConnectRetry; if (error != _note) { _note = error; log?.Invoke("HUD: " + error); } return; }
            _note = "";
            log?.Invoke("HUD: joined SteamVR");
            log?.Invoke("HUD: SteamVR reports the dashboard " + (api.DashboardVisible() ? "open" : "closed") + " at join");
        }
        if (!api.StillRunning()) { Teardown(); _nextConnect = at + ConnectRetry; return; }
        if (_overlay == 0 && !Create()) { _nextConnect = at + ConnectRetry; return; }

        var device = api.ControllerIndex(settings.IsLeft);
        if (device == IOverlayApi.NoDevice)
        {
            // The controller is off or not seen yet: keep the panel out of the way until it is back.
            if (_attachedTo != IOverlayApi.NoDevice) { api.SetVisible(_overlay, false); _attachedTo = IOverlayApi.NoDevice; _panelVisible = false; }
            if (_iconDevice != IOverlayApi.NoDevice && _icon != 0) { api.SetVisible(_icon, false); _iconDevice = IOverlayApi.NoDevice; }
            return;
        }
        // The attached controller must be known before laser events are handled (a grab needs it); the placement is applied afterwards.
        if (_attachedTo == IOverlayApi.NoDevice) Attach(device, _repositioning ? _draftPose : settings.Pose, _repositioning ? _draftWidth : settings.WidthMeters);
        if (_grab is not null) FollowGrab();   // the icon below is placed from the latest hand position
        if (_guideStep == -2) _guideStep = settings.GuideDone ? -1 : settings.Watch ? 0 : 1;
        TickFade(device, at);
        TickOwnLaser(device, at);
        TickIcon(device, at);
        ApplyPanelVisibility();
        if (!_panelVisible) return;
        // The laser only reaches the HUD while the dashboard is open; anything else is ignored so a stray event can never press a button.
        // SteamVR does not always report a dashboard that was already open when the HUD joined, so the laser moving over the panel counts as proof it is open (a click alone never does).
        var events = api.PollEvents(_overlay);
        if (events.Any(e => e.Kind != OverlayEventKind.Other)) _nativeAt = at;
        if (_ownPanel.Count > 0) events = [.. events, .. _ownPanel];
        // The laser leaving the panel is reported, so in hover mode the panel shrinks at once; the hold only covers a missed report.
        foreach (var e in events)
        {
            if (e.Kind is OverlayEventKind.MouseMove or OverlayEventKind.FocusEnter) _laserAt = at;
            else if (e.Kind == OverlayEventKind.FocusLeave) { _laserAt = DateTime.MinValue; _leftByEvent = true; }
        }
        UpdateDashboard(at);
        if (_dashboard) foreach (var overlayEvent in events) Handle(overlayEvent);
        // A release can be missed (the laser left the panel): commit a held slider once its events stop.
        if (_sliding is not null && at - _lastSlideEvent > SlideIdle) CommitSlider();
        if (_slidingWidth is not null && at - _lastSlideEvent > SlideIdle) CommitWidth();
        if (_slidingSetting is not null && at - _lastSlideEvent > SlideIdle) EndSettingSlide();
        if (_grab is not null) FollowGrab();
        var pose = _repositioning ? _draftPose : settings.Pose;
        var width = _repositioning ? _draftWidth : settings.WidthMeters;
        if (device != _attachedTo || pose != _attachedPose || width != _attachedWidth) Attach(device, pose, width);
        Draw(at);
    }

    private readonly record struct AimHit(Over Target, float X, float Y, float Distance, bool Inside, Vector3 Point, Pose Overlay);

    // With the dashboard closed SteamVR gives the overlays no laser. The pointing hand's aim is read through the input system instead: a line and a dot
    // are drawn where it meets the HUD or the watch, and the trigger becomes the click, handed to the same code as SteamVR's own laser events.
    private void TickOwnLaser(uint carrier, DateTime at)
    {
        _ownIcon.Clear(); _ownPanel.Clear();
        if (!settings.OwnLaser || !api.InputAvailable) { EndOwnLaser(); return; }
        // SteamVR's own laser is in use when it sends this HUD mouse events. (The PC reports the dashboard as open all the time on a Steam Link
        // session, so the dashboard flag cannot decide this.)
        if (at - _nativeAt < NativeLaserHold) { OwnLaserNote("SteamVR's laser is on the HUD"); EndOwnLaser(); return; }
        if (!api.TryDevicePose(carrier, out var carrierPose)) { OwnLaserNote("the carrying controller is not tracked"); EndOwnLaser(); return; }
        var left = !settings.IsLeft;
        var pointer = api.ControllerIndex(left);
        if (pointer == IOverlayApi.NoDevice) { OwnLaserNote("the pointing controller is not seen"); EndOwnLaser(); return; }
        if (!api.TryAim(left, _over != Over.None, out var tip, out var pressed)) { OwnLaserNote("no aim from the pointing hand"); EndOwnLaser(); return; }

        AimHit? best = null;
        void Consider(Over target, Pose local, float width, int pixelsWide, int pixelsHigh)
        {
            var world = carrierPose.Then(local);
            // A held press keeps following its target even when the laser slips off the edge, so a slider can be dragged.
            var margin = _ownDown && _over == target ? 2f : HudAim.NearMargin;
            if (_ownDown && _over != target) return;
            if (!HudAim.TryHit(tip, world, width, pixelsWide, pixelsHigh, margin, out var x, out var y, out var distance, out var inside, out var point)) return;
            var hit = new AimHit(target, x, y, distance, inside || (_ownDown && _over == target), point, world);
            if (best is not { } current || (hit.Inside && !current.Inside) || (hit.Inside == current.Inside && hit.Distance < current.Distance)) best = hit;
        }
        if (settings.Watch && _icon != 0 && _iconDevice == carrier)
            Consider(Over.Icon, _repositioning ? _draftIconPose : settings.IconPose, _repositioning ? _draftIconWidth : settings.IconWidthMeters, HudPanel.IconSize, HudPanel.IconSize);
        if (_panelVisible)
            Consider(Over.Panel, _repositioning ? _draftPose : settings.Pose, _repositioning ? _draftWidth : settings.WidthMeters, HudPanel.Width, HudPanel.Height);

        if (best is null)
        {
            // Not near anything: say how far off the ray is, so a wrongly tilted tip shows in the log.
            var aim = tip.Rotate(-Vector3.UnitZ);
            var toPanel = carrierPose.Then(settings.Pose).T - tip.T;
            var off = (toPanel - aim * Vector3.Dot(toPanel, aim)).Length() * 100;
            var local = carrierPose.Inverse().Rotate(aim);
            OwnLaserNote($"aiming, not near the HUD (ray passes {off:0} cm from the panel centre, {Vector3.Dot(toPanel, aim) * 100:0} cm along it; direction in the carrier's space {local.X:0.00},{local.Y:0.00},{local.Z:0.00})");
        }
        else OwnLaserNote($"on the {best.Value.Target} ({best.Value.Distance * 100:0} cm away, {(best.Value.Inside ? "inside" : "near the edge")}), trigger {(pressed ? "down" : "up")}");
        var target = best is { Inside: true } inside ? inside.Target : Over.None;
        void Emit(Over over, OverlayEventKind kind, float x, float y) => (over == Over.Icon ? _ownIcon : _ownPanel).Add(new(kind, pointer, x, y));
        if (target != _over)
        {
            if (_over != Over.None) { if (_ownDown) Emit(_over, OverlayEventKind.MouseUp, 0, 0); Emit(_over, OverlayEventKind.FocusLeave, 0, 0); _ownDown = false; }
            if (best is { } entered && target != Over.None) Emit(target, OverlayEventKind.FocusEnter, entered.X, entered.Y);
            _over = target;
        }
        if (_over != Over.None && best is { } now)
        {
            Emit(_over, OverlayEventKind.MouseMove, now.X, now.Y);
            if (pressed && !_ownDown) { Emit(_over, OverlayEventKind.MouseDown, now.X, now.Y); _ownDown = true; }
            else if (!pressed && _ownDown) { Emit(_over, OverlayEventKind.MouseUp, now.X, now.Y); _ownDown = false; }
        }

        if (best is not { } seen || !api.TryDevicePose(0, out var head)) { api.HideLaser(); return; }
        var line = HudAim.Line(tip.T, seen.Point, head.T, out var length);
        var facing = Vector3.Dot(head.T - seen.Point, seen.Overlay.Z) >= 0 ? 1f : -1f;
        api.ShowLaser(line, length, seen.Overlay with { T = seen.Point + seen.Overlay.Z * (.002f * facing) });
    }

    private string _ownNote = "";
    private DateTime _ownNoteAt = DateTime.MinValue;

    // Written to hud.log when the situation changes (and at most once a second), so a laser that does not appear can be explained from the log.
    private void OwnLaserNote(string what)
    {
        var kind = what.Length > 12 ? what[..12] : what;
        if (kind == _ownNote || _clock - _ownNoteAt < TimeSpan.FromSeconds(1)) return;
        _ownNote = kind; _ownNoteAt = _clock;
        log?.Invoke("HUD laser: " + what);
    }

    private void EndOwnLaser()
    {
        if (_over != Over.None)
        {
            var list = _over == Over.Icon ? _ownIcon : _ownPanel;
            if (_ownDown) list.Add(new(OverlayEventKind.MouseUp, IOverlayApi.NoDevice, 0, 0));
            list.Add(new(OverlayEventKind.FocusLeave, IOverlayApi.NoDevice, 0, 0));
            _over = Over.None; _ownDown = false;
        }
        api.HideLaser();
    }

    private void UpdateDashboard(DateTime at)
    {
        var hover = settings.ExpandOnHover;
        // Carrying the panel or the icon keeps the controls up even when the laser is on the other one.
        // Opened from the watch icon the panel is always fully active; the laser and the dashboard only matter in the always-visible style.
        var open = settings.Watch || _grab is not null || (!hover && api.DashboardVisible()) || at - _laserAt < (hover ? HoverHold : LaserHold);
        if (open == _dashboard) return;
        _dashboard = open;
        log?.Invoke(open ? "HUD: controls shown" : "HUD: readouts only" + (hover ? (_leftByEvent ? " (laser left)" : " (no laser movement)") : ""));
        _leftByEvent = false;
        _expandedAt = open ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        // The picture changes shape, so it is redrawn at once instead of waiting for the next data refresh.
        _drawn = null; _lastUpload = DateTime.MinValue;
        if (open) return;
        // Without input a half-finished interaction could never end: drop it.
        _menu = ""; _page = ""; _sliding = null; _slidingWidth = null; _slidingSetting = null; _hover = null; _drawn = null;
        if (_repositioning) StopRepositioning();
    }

    // Fading out: the HUD and the watch each dim when they face more than the chosen angle away from the viewer (the angle between the
    // direction the picture faces and the line to the headset), or when they are further from the headset than the chosen distance.
    private const float FadeBandDegrees = 4f;
    // The headset's tracked point sits behind the eyes; the angle is measured to the eyes, which matters at arm's length.
    private const float EyeForwardMeters = .14f;
    private float _panelAlpha = 1f, _iconAlpha = 1f;
    private DateTime _fadeAt = DateTime.MinValue;

    private void TickFade(uint device, DateTime at)
    {
        var enabled = settings.FadeAngle > 0 || settings.FadeDistanceCm > 0;
        if (!enabled) { SetAlphas(1f, 1f); return; }
        if (at - _fadeAt < TimeSpan.FromMilliseconds(50)) return;
        _fadeAt = at;
        if (!api.TryDevicePose(0, out var head) || !api.TryDevicePose(device, out var carrier)) return;
        SetAlphas(FadeAlpha(head, carrier.Then(_repositioning ? _draftPose : settings.Pose)), FadeAlpha(head, carrier.Then(_repositioning ? _draftIconPose : settings.IconPose)));
    }

    private float FadeAlpha(Pose head, Pose overlayInWorld)
    {
        var toward = head.T - overlayInWorld.T;
        var distance = toward.Length();
        var alpha = 1f;
        if (settings.FadeDistanceCm > 0)
        {
            var limit = settings.FadeDistanceCm / 100f;
            alpha = Math.Min(alpha, Math.Clamp(1 - (distance - limit) / (limit * .25f), 0f, 1f));
        }
        if (settings.FadeAngle > 0 && distance > 1e-3f)
        {
            var facing = overlayInWorld.Rotate(Vector3.UnitZ);
            var degrees = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(toward + head.Rotate(-Vector3.UnitZ) * EyeForwardMeters), Vector3.Normalize(facing)), -1f, 1f)) * 180f / MathF.PI;
            alpha = Math.Min(alpha, Math.Clamp(1 - (degrees - settings.FadeAngle) / FadeBandDegrees, 0f, 1f));
        }
        return alpha;
    }

    private void SetAlphas(float panel, float icon)
    {
        if (_overlay != 0 && MathF.Abs(panel - _panelAlpha) > .01f) { api.SetAlpha(_overlay, panel); _panelAlpha = panel; }
        if (_icon != 0 && MathF.Abs(icon - _iconAlpha) > .01f) { api.SetAlpha(_icon, icon); _iconAlpha = icon; if (_bubble != 0) api.SetAlpha(_bubble, icon); }
    }

    private bool ShowPanel => !settings.Watch || settings.PanelOpen || _repositioning;

    // The watch: a small round overlay on the controller. Pressing it with the laser opens and closes the panel.
    private void TickIcon(uint device, DateTime at)
    {
        if (!settings.Watch) { DestroyIcon(); return; }
        if (_icon == 0)
        {
            _icon = api.CreateOverlay(IconKey, "SteamFrame HUD button");
            if (_icon == 0) return;
            api.SetAlpha(_icon, 1f); _iconAlpha = 1f;
            api.EnableLaser(_icon, HudPanel.IconSize, HudPanel.IconSize);
            _iconDevice = IOverlayApi.NoDevice; _iconDrawn = null;
            log?.Invoke("HUD: watch icon created");
        }
        var pose = _repositioning ? _draftIconPose : settings.IconPose;
        var width = _repositioning ? _draftIconWidth : settings.IconWidthMeters;
        if (_iconDevice != device || pose != _iconPose || width != _iconWidth)
        {
            api.SetWidth(_icon, width);
            api.AttachToDevice(_icon, device, pose);
            if (_iconDevice != device) api.SetVisible(_icon, true);
            _iconDevice = device; _iconPose = pose; _iconWidth = width;
        }
        var iconEvents = api.PollEvents(_icon);
        if (iconEvents.Any(e => e.Kind != OverlayEventKind.Other)) _nativeAt = at;
        foreach (var e in iconEvents.Concat(_ownIcon))
        {
            if (e.Kind is OverlayEventKind.MouseMove or OverlayEventKind.FocusEnter) _iconHover = true;
            else if (e.Kind == OverlayEventKind.FocusLeave) _iconHover = false;
            else if (_repositioning)
            {
                _laserAt = at;
                // While the HUD is being moved the icon is carried like the panel instead of opening it.
                if (e.Kind == OverlayEventKind.MouseDown) BeginGrab(e.Device, true);
                else if (e.Kind == OverlayEventKind.MouseUp) _grab = null;
            }
            else if (e.Kind == OverlayEventKind.MouseDown)
            {
                settings.PanelOpen = !settings.PanelOpen;
                log?.Invoke("HUD: panel " + (settings.PanelOpen ? "opened" : "closed") + " from the watch icon");
                if (_guideStep == 0 && settings.PanelOpen) _guideStep = 1;
            }
        }
        TickBubble(device, at);
        var view = readView();
        // Everything the face shows is in the key, so the gauge follows the readings as often as the graph does (10 Hz); hover and open are repainted at once.
        var key = $"{view.Codec}|{view.Connected}|{_iconHover}|{settings.PanelOpen}|{view.Mbps:0}|{view.TargetMbps}";
        var touched = _iconDrawn is null || !_iconDrawn.StartsWith($"{view.Codec}|{view.Connected}|{_iconHover}|{settings.PanelOpen}|", StringComparison.Ordinal);
        // Sent again every few seconds even when nothing changed, so an icon SteamVR dropped while it was still starting up comes back.
        if ((key == _iconDrawn && at - _iconUpload < TimeSpan.FromSeconds(3)) || at - _iconUpload < (touched ? InteractionUpload : DataUpload)) return;
        using var bitmap = HudPanel.RenderIcon(view, _iconHover, settings.PanelOpen);
        api.SetImage(_icon, HudPanel.ToRgba(bitmap), HudPanel.IconSize, HudPanel.IconSize);
        _iconDrawn = key; _iconUpload = at;
    }

    // Step 0 of the guide: a bubble above the watch, in the watch's own orientation, whose arrow points down at it.
    private void TickBubble(uint device, DateTime at)
    {
        var show = _guideStep == 0 && !settings.PanelOpen && !_repositioning;
        if (!show) { DestroyBubble(); return; }
        if (_bubble == 0)
        {
            _bubble = api.CreateOverlay(BubbleKey, "SteamFrame HUD guide");
            if (_bubble == 0) return;
            api.SetAlpha(_bubble, _iconAlpha);
            using var picture = HudPanel.RenderWatchGuide();
            api.SetImage(_bubble, HudPanel.ToRgba(picture), HudPanel.GuideBubbleWidth, HudPanel.GuideBubbleHeight);
            api.SetVisible(_bubble, true);
            log?.Invoke("HUD: guide bubble shown at the watch");
        }
        var iconWidth = _iconWidth > 0 ? _iconWidth : settings.IconWidthMeters;
        var width = Math.Max(.20f, iconWidth * 4.6f);
        var height = width * HudPanel.GuideBubbleHeight / HudPanel.GuideBubbleWidth;
        // The bubble's lower tip sits a little above the watch's top edge.
        var local = settings.IconPose.Then(new Pose(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(0, iconWidth / 2f + .012f + height / 2f, 0)));
        if (_bubbleAt != local || _bubbleWidth != width || _bubbleDevice != device)
        {
            api.SetWidth(_bubble, width);
            api.AttachToDevice(_bubble, device, local);
            _bubbleAt = local; _bubbleWidth = width; _bubbleDevice = device;
        }
    }

    private void DestroyBubble()
    {
        if (_bubble != 0 && api.Connected) { try { api.DestroyOverlay(_bubble); } catch (Exception) { /* SteamVR went first */ } }
        _bubble = 0; _bubbleWidth = 0; _bubbleDevice = IOverlayApi.NoDevice;
    }

    private void DestroyIcon()
    {
        DestroyBubble();
        if (_icon != 0 && api.Connected) { try { api.DestroyOverlay(_icon); } catch (Exception) { /* SteamVR went first */ } }
        _icon = 0; _iconDevice = IOverlayApi.NoDevice; _iconDrawn = null; _iconHover = false;
    }

    // In Watch style the panel is only there while it is open; closing it drops any half-finished interaction.
    private void ApplyPanelVisibility()
    {
        if (_attachedTo == IOverlayApi.NoDevice || ShowPanel == _panelVisible) return;
        _panelVisible = ShowPanel;
        api.SetVisible(_overlay, _panelVisible);
        _drawn = null;
        if (_panelVisible) return;
        _menu = ""; _page = ""; _sliding = null; _slidingWidth = null; _hover = null; _grab = null; _dashboard = false;
        if (_repositioning) StopRepositioning();
    }

    public void Teardown()
    {
        _grab = null;
        _over = Over.None; _ownDown = false;
        if (api.Connected) api.HideLaser();
        DestroyIcon();
        if (_overlay != 0 && api.Connected) { try { api.DestroyOverlay(_overlay); } catch (Exception) { /* SteamVR went first */ } }
        _overlay = 0; _attachedTo = IOverlayApi.NoDevice; _drawn = null; _panelVisible = false;
        if (api.Connected) api.Disconnect();
    }

    public void Dispose() { Teardown(); api.Dispose(); }

    private bool Create()
    {
        var handle = api.CreateOverlay(OverlayKey, "SteamFrame HUD");
        if (handle == 0) { log?.Invoke("HUD: the overlay could not be created."); return false; }
        _overlay = handle;
        log?.Invoke("HUD: overlay created");
        api.SetAlpha(handle, 1f); _panelAlpha = 1f;
        api.EnableLaser(handle, HudPanel.Width, HudPanel.Height);
        _drawn = null; _attachedTo = IOverlayApi.NoDevice;
        return true;
    }

    private void Attach(uint device, Pose pose, float width)
    {
        api.SetWidth(_overlay, width);
        api.AttachToDevice(_overlay, device, pose);
        if (_attachedTo == IOverlayApi.NoDevice) { _panelVisible = ShowPanel; api.SetVisible(_overlay, _panelVisible); log?.Invoke($"HUD: {(_panelVisible ? "shown" : "ready")} on device {device}"); }
        _attachedTo = device; _attachedPose = pose; _attachedWidth = width;
    }

    private HudView View()
    {
        var view = readView() with { Repositioning = _repositioning, Hand = settings.IsLeft ? "left" : "right", OpenMenu = _menu, Interactive = _dashboard,
            Page = _page, AutoShow = settings.AutoShow, ExpandOnHover = settings.ExpandOnHover, Watch = settings.Watch, Target = _resizeIcon ? "icon" : "panel", FadeAngle = settings.FadeAngle, FadeDistance = settings.FadeDistanceCm, Guide = _guideStep >= 1 && !_repositioning ? _guideStep : 0, OwnLaser = settings.OwnLaser };
        if (_slidingWidth is { } heldWidth) view = view with { WidthPx = heldWidth };
        else if (_committedWidth is { } chosenWidth && view.WidthPx != chosenWidth && _clock - _committedAt < CommitHold) view = view with { WidthPx = chosenWidth };
        if (_sliding is { } held) return view with { TargetMbps = held };
        if (_committed is { } chosen && view.TargetMbps != chosen && _clock - _committedAt < CommitHold) return view with { TargetMbps = chosen };
        return view;
    }

    private void Draw(DateTime at)
    {
        var view = View();
        var hoverChanged = _hover != _drawnHover;
        if (_drawn is not null && view == _drawn && !hoverChanged) return;
        // The picture lives in one texture, so repainting costs nothing visible: anything the user did shows at once, readouts at 10 Hz.
        var touched = hoverChanged || _drawn is null || ControlsChanged(_drawn, view);
        if (at - _lastUpload < (touched ? InteractionUpload : DataUpload)) return;
        using var bitmap = HudPanel.Render(view, _hover);
        api.SetImage(_overlay, HudPanel.ToRgba(bitmap), HudPanel.Width, HudPanel.Height);
        _drawn = view; _drawnHover = _hover; _lastUpload = at;
        if (_expandedAt != 0 && view.Interactive)
        {
            log?.Invoke($"HUD: expanded picture handed to SteamVR {System.Diagnostics.Stopwatch.GetElapsedTime(_expandedAt).TotalMilliseconds:0} ms after the laser event was read");
            _expandedAt = 0;
        }
    }

    private static bool ControlsChanged(HudView was, HudView now) =>
        was.OpenMenu != now.OpenMenu || was.Page != now.Page || was.AutoShow != now.AutoShow || was.ExpandOnHover != now.ExpandOnHover || was.OwnLaser != now.OwnLaser || was.Watch != now.Watch || was.Target != now.Target || was.FadeAngle != now.FadeAngle || was.FadeDistance != now.FadeDistance || was.Interactive != now.Interactive || was.Repositioning != now.Repositioning || was.TargetMbps != now.TargetMbps || was.WidthPx != now.WidthPx
        || was.Notice != now.Notice || was.H264Selected != now.H264Selected || was.Busy != now.Busy || was.RateControl != now.RateControl || was.Preset != now.Preset || was.VbvFrames != now.VbvFrames || was.MultiPass != now.MultiPass || was.Guide != now.Guide;

    private void Handle(OverlayEvent e)
    {
        var y = settings.FlipLaserY ? HudPanel.Height - e.Y : e.Y;
        var hit = HudPanel.HitTest(View(), e.X, y);
        if (_slidingSetting is not null)
        {
            // A held settings slider follows the laser's x anywhere on the panel and takes effect at once; letting go saves it.
            if (e.Kind == OverlayEventKind.MouseUp) EndSettingSlide();
            else if (e.Kind == OverlayEventKind.MouseMove) { MoveSettingSlide(e.X); _lastSlideEvent = _clock; _hover = _slidingSetting; }
            return;
        }
        if (_slidingWidth is not null)
        {
            if (e.Kind == OverlayEventKind.MouseUp) { CommitWidth(); return; }
            if (e.Kind == OverlayEventKind.MouseMove) { _slidingWidth = HudPanel.ChoiceAt(HudPanel.WidthChoices, e.X); _lastSlideEvent = _clock; _hover = "wslider"; }
            return;
        }
        if (_sliding is not null)
        {
            // A held slider follows the laser's x anywhere on the panel; letting go commits.
            if (e.Kind == OverlayEventKind.MouseUp) { CommitSlider(); return; }
            if (e.Kind == OverlayEventKind.MouseMove) { _sliding = HudPanel.SliderValue(e.X); _lastSlideEvent = _clock; _hover = "slider"; }
            return;
        }
        switch (e.Kind)
        {
            case OverlayEventKind.MouseMove:
                _hover = hit;
                break;
            case OverlayEventKind.MouseDown when hit is "slide:angle" or "slide:dist":
                _slidingSetting = hit; _lastSlideEvent = _clock; MoveSettingSlide(e.X);
                break;
            case OverlayEventKind.MouseDown when hit == "wslider":
                _slidingWidth = HudPanel.ChoiceAt(HudPanel.WidthChoices, e.X); _lastSlideEvent = _clock;
                break;
            case OverlayEventKind.MouseDown when hit == "slider":
                _sliding = HudPanel.SliderValue(e.X); _lastSlideEvent = _clock;
                break;
            case OverlayEventKind.MouseDown when hit == "grab":
                if (_repositioning) BeginGrab(e.Device, false);
                break;
            case OverlayEventKind.MouseDown when hit is not null:
                Press(hit);
                break;
            case OverlayEventKind.MouseUp:
                _grab = null;
                break;
        }
    }

    private void Press(string id)
    {
        var view = View();
        var parts = id.Split(':');
        switch (parts[0])
        {
            case "codec": Fire(() => actions.SetCodecAsync(parts[1] == "h264")); break;
            case "menu": _menu = parts[1] == "close" ? "" : parts[1]; _drawn = null; break;
            case "rc": _menu = ""; Fire(() => actions.SetEncoderAsync(parts[1], view.Preset, view.VbvFrames)); break;
            case "preset": _menu = ""; Fire(() => actions.SetEncoderAsync(view.RateControl, int.Parse(parts[1]), view.VbvFrames)); break;
            case "vbv": _menu = ""; Fire(() => actions.SetEncoderAsync(view.RateControl, view.Preset, int.Parse(parts[1]))); break;
            case "mp": _menu = ""; Fire(() => actions.SetMultiPassAsync(int.Parse(parts[1]))); break;
            case "guide": Guide(parts[1]); break;
            case "reposition": StartRepositioning(); break;
            case "hide": Fire(actions.HideAsync); break;
            case "counters": Fire(actions.ResetCountersAsync); break;
            case "page": _page = parts[1] == "close" ? "" : parts[1]; _menu = ""; _drawn = null; break;
            case "set":
                if (parts[1] == "autoshow") settings.AutoShow = parts[2] == "on";
                else if (parts[1] == "expand") settings.ExpandOnHover = parts[2] == "hover";
                else if (parts[1] == "ownlaser") settings.OwnLaser = parts[2] == "on";
                else if (parts[1] == "style") { settings.Watch = parts[2] == "watch"; settings.PanelOpen = settings.Watch; }
                Persist(); _drawn = null;
                break;
            case "size":
                var factor = parts[1] == "+" ? 1.12f : 1 / 1.12f;
                if (_resizeIcon) _draftIconWidth = Math.Clamp(_draftIconWidth * factor, HudSettings.IconMinimumWidth, HudSettings.IconMaximumWidth);
                else _draftWidth = Math.Clamp(_draftWidth * factor, HudSettings.MinimumWidth, HudSettings.MaximumWidth);
                break;
            case "save": SavePlacement(); break;
            case "cancel": StopRepositioning(); break;
            case "default":
                settings.ResetPlacement(); Persist();
                _draftPose = settings.Pose; _draftWidth = settings.WidthMeters;
                _draftIconPose = settings.IconPose; _draftIconWidth = settings.IconWidthMeters;
                break;
        }
    }

    private void Guide(string what)
    {
        if (what == "replay") { _guideStep = 1; _page = ""; _menu = ""; _drawn = null; return; }
        if (what == "next" && _guideStep < HudPanel.GuideSteps) { _guideStep++; _drawn = null; return; }
        _guideStep = -1; settings.GuideDone = true; _drawn = null;   // Finish or Skip
        Persist();
        log?.Invoke("HUD: guide finished");
    }

    private void MoveSettingSlide(float x)
    {
        if (_slidingSetting == "slide:angle") settings.FadeAngle = HudPanel.ChoiceAt(HudPanel.FadeAngleChoices, x);
        else settings.FadeDistanceCm = HudPanel.ChoiceAt(HudPanel.FadeDistanceChoices, x);
    }

    private void EndSettingSlide()
    {
        _slidingSetting = null;
        Persist();
    }

    private void CommitSlider()
    {
        if (_sliding is not { } chosen) return;
        _sliding = null;
        if (chosen == readView().TargetMbps) return;
        _committed = chosen; _committedAt = _clock;
        Fire(() => actions.SetBitrateAsync(chosen));
    }

    private void CommitWidth()
    {
        if (_slidingWidth is not { } chosen) return;
        _slidingWidth = null;
        if (chosen == StreamWidth.Snapped(readView().WidthPx)) return;
        _committedWidth = chosen; _committedAt = _clock;
        Fire(() => actions.SetWidthAsync(chosen));
    }

    private void StartRepositioning()
    {
        _repositioning = true; _menu = ""; _draftPose = settings.Pose; _draftWidth = settings.WidthMeters; _drawn = null;
        _draftIconPose = settings.IconPose; _draftIconWidth = settings.IconWidthMeters; _resizeIcon = false;
    }

    private void StopRepositioning()
    {
        _repositioning = false; _grab = null; _drawn = null;
    }

    private void SavePlacement()
    {
        settings.SetPlacement(_draftPose, _draftWidth);
        settings.SetIconPlacement(_draftIconPose, _draftIconWidth);
        Persist();
        StopRepositioning();
    }

    private void Persist()
    {
        try { saveSettings(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log?.Invoke("HUD: could not save its place: " + error.Message); }
    }

    // Runs on the caller's (UI) thread, so the tray's own handlers keep their threading rules.
    private void Fire(Func<Task> action) => _ = Run(action);

    private async Task Run(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is not OperationCanceledException) { log?.Invoke("HUD action failed: " + error.Message); }
    }

    // The panel is carried by the grabbing laser's controller while the trigger is held, then its place is re-expressed on the carrier controller.
    private void BeginGrab(uint grabber, bool icon)
    {
        if (grabber == _attachedTo || !api.TryDevicePose(grabber, out var grabbed) || !api.TryDevicePose(_attachedTo, out var carrier)) return;
        _grab = new(grabber, grabbed, carrier.Then(icon ? _draftIconPose : _draftPose), icon);
        _resizeIcon = icon; _drawn = null;
    }

    private void FollowGrab()
    {
        if (_grab is null) return;
        if (!api.TryDevicePose(_grab.Device, out var grabbed) || !api.TryDevicePose(_attachedTo, out var carrier)) return;
        var panelWorld = grabbed.Then(_grab.DeviceAtGrab.Inverse()).Then(_grab.PanelAtGrab);
        var placed = carrier.Inverse().Then(panelWorld).Orthonormal();
        if (_grab.Icon) _draftIconPose = placed; else _draftPose = placed;
    }
}
