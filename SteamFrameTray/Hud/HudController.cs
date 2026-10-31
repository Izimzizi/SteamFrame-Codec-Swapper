namespace SteamFrameTray;

/// <summary>What a press on the HUD does. The tray implements these with the same calls as its menu.</summary>
public interface IHudActions
{
    Task SetCodecAsync(bool h264);
    Task SetBitrateAsync(int mbps);
    Task SetEncoderAsync(string rateControl, int preset, int vbvFrames);
}

/// <summary>
/// Keeps the HUD overlay in step with SteamVR: joins when SteamVR runs, hangs the panel on a controller, redraws it when its readouts change,
/// turns laser clicks into actions, and lets the panel be dragged to a new place on the controller.
/// </summary>
public sealed class HudController(IOverlayApi api, HudSettings settings, Func<HudView> readView, IHudActions actions, Action saveSettings, Func<bool> steamVrRunning, Action<string>? log = null) : IDisposable
{
    private const string OverlayKey = "steamframe.tray.hud";
    private static readonly TimeSpan ConnectRetry = TimeSpan.FromSeconds(3), MinimumUpload = TimeSpan.FromMilliseconds(200);

    private ulong _overlay;
    private uint _attachedTo = IOverlayApi.NoDevice;
    private Pose _attachedPose;
    private float _attachedWidth;
    private HudView? _drawn;
    private string? _drawnHover, _hover;
    private DateTime _lastUpload = DateTime.MinValue, _nextConnect = DateTime.MinValue;
    private bool _repositioning;
    private Pose _draftPose;
    private float _draftWidth;
    private Grab? _grab;
    private string _note = "";

    private sealed record Grab(uint Device, Pose DeviceAtGrab, Pose PanelAtGrab);

    public bool Repositioning => _repositioning;
    public bool Attached => _overlay != 0;

    /// <summary>Call about 30 times a second from the UI thread.</summary>
    public void Tick(DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        if (!settings.Enabled || !steamVrRunning()) { Teardown(); return; }
        if (!api.Connected)
        {
            if (at < _nextConnect) return;
            if (!api.TryConnect(out var error)) { _nextConnect = at + ConnectRetry; if (error != _note) { _note = error; log?.Invoke("HUD: " + error); } return; }
            _note = "";
        }
        if (!api.StillRunning()) { Teardown(); _nextConnect = at + ConnectRetry; return; }
        if (_overlay == 0 && !Create()) { _nextConnect = at + ConnectRetry; return; }

        var device = api.ControllerIndex(settings.IsLeft);
        if (device == IOverlayApi.NoDevice)
        {
            // The controller is off or not seen yet: keep the panel out of the way until it is back.
            if (_attachedTo != IOverlayApi.NoDevice) { api.SetVisible(_overlay, false); _attachedTo = IOverlayApi.NoDevice; }
            return;
        }
        // The attached controller must be known before laser events are handled (a grab needs it); the placement is applied afterwards.
        if (_attachedTo == IOverlayApi.NoDevice) Attach(device, _repositioning ? _draftPose : settings.Pose, _repositioning ? _draftWidth : settings.WidthMeters);
        foreach (var overlayEvent in api.PollEvents(_overlay)) Handle(overlayEvent);
        if (_grab is not null) FollowGrab();
        var pose = _repositioning ? _draftPose : settings.Pose;
        var width = _repositioning ? _draftWidth : settings.WidthMeters;
        if (device != _attachedTo || pose != _attachedPose || width != _attachedWidth) Attach(device, pose, width);
        Draw(at);
    }

    public void Teardown()
    {
        _grab = null;
        if (_overlay != 0 && api.Connected) { try { api.DestroyOverlay(_overlay); } catch (Exception) { /* SteamVR went first */ } }
        _overlay = 0; _attachedTo = IOverlayApi.NoDevice; _drawn = null;
        if (api.Connected) api.Disconnect();
    }

    public void Dispose() { Teardown(); api.Dispose(); }

    private bool Create()
    {
        var handle = api.CreateOverlay(OverlayKey, "SteamFrame HUD");
        if (handle == 0) { log?.Invoke("HUD: the overlay could not be created."); return false; }
        _overlay = handle;
        api.SetAlpha(handle, 1f);
        api.EnableLaser(handle, HudPanel.Width, HudPanel.Height);
        _drawn = null; _attachedTo = IOverlayApi.NoDevice;
        return true;
    }

    private void Attach(uint device, Pose pose, float width)
    {
        api.SetWidth(_overlay, width);
        api.AttachToDevice(_overlay, device, pose);
        if (_attachedTo == IOverlayApi.NoDevice) api.SetVisible(_overlay, true);
        _attachedTo = device; _attachedPose = pose; _attachedWidth = width;
    }

    private HudView View() => readView() with { Repositioning = _repositioning, Hand = settings.IsLeft ? "left" : "right" };

    private void Draw(DateTime at)
    {
        var view = View();
        if (_drawn is not null && view == _drawn && _hover == _drawnHover) return;
        if (at - _lastUpload < MinimumUpload) return;
        using var bitmap = HudPanel.Render(view, _hover);
        api.SetImage(_overlay, HudPanel.ToRgba(bitmap), HudPanel.Width, HudPanel.Height);
        _drawn = view; _drawnHover = _hover; _lastUpload = at;
    }

    private void Handle(OverlayEvent e)
    {
        var y = settings.FlipLaserY ? HudPanel.Height - e.Y : e.Y;
        var hit = HudPanel.HitTest(View(), e.X, y);
        switch (e.Kind)
        {
            case OverlayEventKind.MouseMove:
                _hover = hit;
                break;
            case OverlayEventKind.MouseDown when hit == "grab":
                if (_repositioning) BeginGrab(e.Device);
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
            case "rate":
                var target = Math.Clamp(view.TargetMbps + (parts[1] == "+" ? HudPanel.BitrateStep : -HudPanel.BitrateStep), TrayController.MinimumMbps, TrayController.MaximumMbps);
                if (target != view.TargetMbps) Fire(() => actions.SetBitrateAsync(target));
                break;
            case "rc": Fire(() => actions.SetEncoderAsync(parts[1], view.Preset, view.VbvFrames)); break;
            case "preset": Fire(() => actions.SetEncoderAsync(view.RateControl, int.Parse(parts[1]), view.VbvFrames)); break;
            case "vbv": Fire(() => actions.SetEncoderAsync(view.RateControl, view.Preset, int.Parse(parts[1]))); break;
            case "reposition": StartRepositioning(); break;
            case "size": _draftWidth = Math.Clamp(_draftWidth * (parts[1] == "+" ? 1.12f : 1 / 1.12f), HudSettings.MinimumWidth, HudSettings.MaximumWidth); break;
            case "save": SavePlacement(); break;
            case "cancel": StopRepositioning(); break;
            case "default":
                settings.ResetPlacement(); Persist();
                _draftPose = settings.Pose; _draftWidth = settings.WidthMeters;
                break;
        }
    }

    private void StartRepositioning()
    {
        _repositioning = true; _draftPose = settings.Pose; _draftWidth = settings.WidthMeters; _drawn = null;
    }

    private void StopRepositioning()
    {
        _repositioning = false; _grab = null; _drawn = null;
    }

    private void SavePlacement()
    {
        settings.SetPlacement(_draftPose, _draftWidth);
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
    private void BeginGrab(uint grabber)
    {
        if (grabber == _attachedTo || !api.TryDevicePose(grabber, out var grabbed) || !api.TryDevicePose(_attachedTo, out var carrier)) return;
        _grab = new(grabber, grabbed, carrier.Then(_draftPose));
    }

    private void FollowGrab()
    {
        if (_grab is null) return;
        if (!api.TryDevicePose(_grab.Device, out var grabbed) || !api.TryDevicePose(_attachedTo, out var carrier)) return;
        var panelWorld = grabbed.Then(_grab.DeviceAtGrab.Inverse()).Then(_grab.PanelAtGrab);
        _draftPose = carrier.Inverse().Then(panelWorld).Orthonormal();
    }
}
