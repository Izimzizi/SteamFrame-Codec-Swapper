using System.Numerics;

namespace CodecSwapper;

/// <summary>What a press on the HUD does. The tray implements these with the same calls as its menu.</summary>
public interface IHudActions
{
    Task SetCodecAsync(bool h264);
    Task SetBitrateAsync(int mbps);
    Task SetEncoderAsync(string rateControl, int preset, int vbvFrames);
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
    private const string OverlayKey = "steamframe.tray.hud", IconKey = "steamframe.tray.hud.icon";
    private static readonly TimeSpan ConnectRetry = TimeSpan.FromSeconds(3), DataUpload = TimeSpan.FromMilliseconds(100), InteractionUpload = TimeSpan.FromMilliseconds(25), SlideIdle = TimeSpan.FromMilliseconds(600), CommitHold = TimeSpan.FromSeconds(4);

    private ulong _overlay, _icon;
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
    private int? _sliding;                 // the bitrate under the laser while the slider is held
    private int? _committed;               // the bitrate just chosen, shown until the tray reports it
    private DateTime _committedAt, _lastSlideEvent, _clock;

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
        TickFade(device, at);
        TickIcon(device, at);
        ApplyPanelVisibility();
        if (!_panelVisible) return;
        // The laser only reaches the HUD while the dashboard is open; anything else is ignored so a stray event can never press a button.
        // SteamVR does not always report a dashboard that was already open when the HUD joined, so the laser moving over the panel counts as proof it is open (a click alone never does).
        var events = api.PollEvents(_overlay);
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
        if (_grab is not null) FollowGrab();
        var pose = _repositioning ? _draftPose : settings.Pose;
        var width = _repositioning ? _draftWidth : settings.WidthMeters;
        if (device != _attachedTo || pose != _attachedPose || width != _attachedWidth) Attach(device, pose, width);
        Draw(at);
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
        _menu = ""; _page = ""; _sliding = null; _hover = null; _drawn = null;
        if (_repositioning) StopRepositioning();
    }

    // Fading out: the HUD and the watch each dim when they face more than the chosen angle away from the viewer (the angle between the
    // direction the picture faces and the line to the headset), or when they are further from the headset than the chosen distance.
    private const float FadeBandDegrees = 12f;
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
            var degrees = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(toward), Vector3.Normalize(facing)), -1f, 1f)) * 180f / MathF.PI;
            alpha = Math.Min(alpha, Math.Clamp(1 - (degrees - settings.FadeAngle) / FadeBandDegrees, 0f, 1f));
        }
        return alpha;
    }

    private void SetAlphas(float panel, float icon)
    {
        if (_overlay != 0 && MathF.Abs(panel - _panelAlpha) > .01f) { api.SetAlpha(_overlay, panel); _panelAlpha = panel; }
        if (_icon != 0 && MathF.Abs(icon - _iconAlpha) > .01f) { api.SetAlpha(_icon, icon); _iconAlpha = icon; }
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
        foreach (var e in api.PollEvents(_icon))
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
            else if (e.Kind == OverlayEventKind.MouseDown) { settings.PanelOpen = !settings.PanelOpen; log?.Invoke("HUD: panel " + (settings.PanelOpen ? "opened" : "closed") + " from the watch icon"); }
        }
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

    private void DestroyIcon()
    {
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
        _menu = ""; _page = ""; _sliding = null; _hover = null; _grab = null; _dashboard = false;
        if (_repositioning) StopRepositioning();
    }

    public void Teardown()
    {
        _grab = null;
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
            Page = _page, AutoShow = settings.AutoShow, ExpandOnHover = settings.ExpandOnHover, Watch = settings.Watch, Target = _resizeIcon ? "icon" : "panel", FadeAngle = settings.FadeAngle, FadeDistance = settings.FadeDistanceCm };
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
        was.OpenMenu != now.OpenMenu || was.Page != now.Page || was.AutoShow != now.AutoShow || was.ExpandOnHover != now.ExpandOnHover || was.Watch != now.Watch || was.Target != now.Target || was.FadeAngle != now.FadeAngle || was.FadeDistance != now.FadeDistance || was.Interactive != now.Interactive || was.Repositioning != now.Repositioning || was.TargetMbps != now.TargetMbps
        || was.Notice != now.Notice || was.H264Selected != now.H264Selected || was.Busy != now.Busy || was.RateControl != now.RateControl || was.Preset != now.Preset || was.VbvFrames != now.VbvFrames;

    private void Handle(OverlayEvent e)
    {
        var y = settings.FlipLaserY ? HudPanel.Height - e.Y : e.Y;
        var hit = HudPanel.HitTest(View(), e.X, y);
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
            case "reposition": StartRepositioning(); break;
            case "hide": Fire(actions.HideAsync); break;
            case "counters": Fire(actions.ResetCountersAsync); break;
            case "page": _page = parts[1] == "close" ? "" : parts[1]; _menu = ""; _drawn = null; break;
            case "set":
                if (parts[1] == "autoshow") settings.AutoShow = parts[2] == "on";
                else if (parts[1] == "expand") settings.ExpandOnHover = parts[2] == "hover";
                else if (parts[1] == "fadeangle") settings.FadeAngle = int.Parse(parts[2]);
                else if (parts[1] == "fadedist") settings.FadeDistanceCm = int.Parse(parts[2]);
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

    private void CommitSlider()
    {
        if (_sliding is not { } chosen) return;
        _sliding = null;
        if (chosen == readView().TargetMbps) return;
        _committed = chosen; _committedAt = _clock;
        Fire(() => actions.SetBitrateAsync(chosen));
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
