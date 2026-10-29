using System.Text.Json;
using SteamFrame.Core;

namespace SteamFrameTray;

public enum TrayState { Disarmed, Arming, Armed, Waiting, Disarming, Error }

/// <summary>What a bitrate change ended up doing.</summary>
public enum ChangeOutcome { Saved, AppliedLive, Reapplied, Declined }

public sealed record TraySnapshot(TrayState State, string Message, int TargetMbps)
{
    /// <summary>The user has asked for H.264 to be kept (a session is starting, active or waiting for SteamVR).</summary>
    public bool IsArmed => State is TrayState.Arming or TrayState.Armed or TrayState.Waiting;
}

/// <summary>
/// Arm = keep H.264 applied across SteamVR restarts at the chosen bitrate (waiting for SteamVR when it is closed).
/// Disarm = stock HEVC and the original SteamVR streaming settings. Refresh rate, multi-link and automatic bandwidth
/// are left to SteamVR.
/// </summary>
public sealed class TrayController
{
    /// <summary>The unpatched Steam Link driver caps SteamVR's target here; above it the bitrate patch is required.</summary>
    public const int PatchFreeMaxMbps = 232;
    public const int MinimumMbps = 25, MaximumMbps = 500;

    private readonly ICodecService _codec;
    private readonly ICredentialVault _vault;
    private readonly IDriverPatch _patch;
    private readonly ILiveBitrate? _live;
    private readonly FramePreferences _preferences;
    private readonly Func<Task> _save;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _wasLive;
    private int? _lastSteamVr;
    private DateTime _ownWriteUntil;

    public TrayController(ICodecService codec, ICredentialVault vault, IDriverPatch patch, FramePreferences preferences, Func<Task> save, ILiveBitrate? live = null)
    {
        _codec = codec; _vault = vault; _patch = patch; _preferences = preferences; _save = save; _live = live;
        Current = new(TrayState.Disarmed, "Disarmed. SteamVR uses its own codec and bitrate.", TargetMbps);
    }

    public event Action<TraySnapshot>? Changed;
    public TraySnapshot Current { get; private set; }
    public int TargetMbps => Math.Clamp(_preferences.CodecTargetMbps, MinimumMbps, MaximumMbps);
    public bool WantsArmed => _preferences.KeepH264;
    public bool SteamVrRunning => _live?.SteamVrRunning ?? false;
    public bool HasPassword
    {
        get { try { return !string.IsNullOrEmpty(_vault.Load()); } catch (Exception error) when (error is System.ComponentModel.Win32Exception) { return false; } }
    }

    public async Task SetTargetAsync(int mbps)
    {
        if (mbps is < MinimumMbps or > MaximumMbps) throw new ArgumentOutOfRangeException(nameof(mbps), $"Choose {MinimumMbps}-{MaximumMbps} Mbps.");
        _preferences.CodecTargetMbps = mbps;
        await _save();
        Publish(Current with { TargetMbps = mbps });
    }

    /// <summary>
    /// Changes the bitrate. A running session takes it live (no SteamVR restart); a session that cannot
    /// (an older supervisor, or one whose hook is not live) is re-applied after the user agrees to the restart.
    /// </summary>
    public async Task<ChangeOutcome> ChangeTargetAsync(int mbps, Func<Task<string?>> requestPassword, Func<BitratePatchStatus, Task<bool>> confirmPatch,
        Func<Task<bool>> confirmRestart, CancellationToken cancellationToken = default)
    {
        await SetTargetAsync(mbps);
        // SteamVR flushes its settings file later than a live write, so a reading right after our own change is stale.
        _ownWriteUntil = DateTime.UtcNow.AddSeconds(10);
        if (!Current.IsArmed) return await ChangeDisarmedAsync(mbps, confirmPatch, cancellationToken);
        var status = await _codec.ReadStatusAsync(cancellationToken);
        if (status.Live && status.RateLive && status.State is "ready" or "waiting")
        {
            await EnsureBitratePatchAsync(mbps, confirmPatch);
            await ChangeRateLiveAsync(mbps, cancellationToken);
            return ChangeOutcome.AppliedLive;
        }
        return await ReapplyAsync(requestPassword, confirmPatch, confirmRestart, cancellationToken);
    }

    /// <summary>
    /// Applies the saved encoder options (rate control, preset, VBV) to a live H.264 session: the hook queues them and the supervisor
    /// restarts the Frame's stream so SteamVR builds a new encoder with them. False means they are used the next time H.264 is armed.
    /// </summary>
    public async Task<bool> ApplyEncoderOptionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Current.IsArmed) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var status = await _codec.ReadStatusAsync(cancellationToken);
            if (!(status.Live && status.RateLive && status.ActiveCodec == "H.264" && status.State == "ready")) return false;
            var result = await _codec.SetEncoderAsync(_preferences.HookRateControl, _preferences.HookPreset, _preferences.HookVbvFrames, new Progress<CodecServiceStatus>(), cancellationToken);
            Publish(Map(result));
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stock codec: SteamVR's own setting is the bitrate, so write it live, or into its settings file while SteamVR is closed.</summary>
    private async Task<ChangeOutcome> ChangeDisarmedAsync(int mbps, Func<BitratePatchStatus, Task<bool>> confirmPatch, CancellationToken cancellationToken)
    {
        if (_live is null) return ChangeOutcome.Saved;
        await EnsureBitratePatchAsync(mbps, confirmPatch);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_live.SteamVrRunning)
            {
                try { await _live.ApplyAsync(mbps); }
                catch (Exception error) when (error is IOException or InvalidOperationException)
                {
                    throw new InvalidOperationException($"The bitrate was saved, but SteamVR's live setting could not be changed ({error.Message}).", error);
                }
                Publish(Current with { Message = $"Bitrate set to {mbps} Mbps." });
                return ChangeOutcome.AppliedLive;
            }
            await _live.WriteFileAsync(mbps);
            Publish(Current with { Message = $"Bitrate set to {mbps} Mbps for the next SteamVR start." });
            return ChangeOutcome.Saved;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// SteamVR's slider is the source of truth: when it moves on its own, the tray follows (and tells a running
    /// session so the value SteamVR restarts with is the one the user chose).
    /// </summary>
    private async Task FollowSteamVrAsync(CodecServiceStatus status, CancellationToken cancellationToken)
    {
        if (_live is null || DateTime.UtcNow < _ownWriteUntil) return;
        double? read;
        try { read = await _live.ReadTargetAsync(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException) { return; }
        if (read is not double value || !double.IsFinite(value)) return;
        var mbps = (int)Math.Round(value);
        var previous = _lastSteamVr;
        if (mbps is < MinimumMbps or > MaximumMbps) return;
        _lastSteamVr = mbps;
        // Only a change in what SteamVR reports counts; a stale reading of its old value after our own write is not one.
        if (previous == mbps || mbps == TargetMbps) return;
        if (_gate.CurrentCount == 0) { _lastSteamVr = previous; return; }
        if (Current.IsArmed)
        {
            if (!(status.Live && status.RateLive && status.State is "ready" or "waiting")) return;
            try { await _codec.SetRateAsync(mbps, new Progress<CodecServiceStatus>(), cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException) { return; }
            _preferences.CodecTargetMbps = mbps;
            await _save();
            Publish(Map(await _codec.ReadStatusAsync(cancellationToken)));
            return;
        }
        _preferences.CodecTargetMbps = mbps;
        await _save();
        Publish(Current with { TargetMbps = mbps });
    }

    private async Task<ChangeOutcome> ReapplyAsync(Func<Task<string?>> requestPassword, Func<BitratePatchStatus, Task<bool>> confirmPatch,
        Func<Task<bool>> confirmRestart, CancellationToken cancellationToken)
    {
        // The saved choice is applied the next time H.264 is armed if the user would rather not restart SteamVR now.
        if (_live?.SteamVrRunning != false && !await confirmRestart()) return ChangeOutcome.Declined;
        // The restart was just agreed to, so arming does not ask again.
        await ArmAsync(requestPassword, confirmPatch, null, cancellationToken);
        return ChangeOutcome.Reapplied;
    }

    private async Task ChangeRateLiveAsync(int mbps, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Publish(new(TrayState.Arming, $"Changing the bitrate to {mbps} Mbps…", TargetMbps));
            var result = await _codec.SetRateAsync(mbps, new Progress<CodecServiceStatus>(), cancellationToken);
            Publish(Map(result));
            // The supervisor has recorded the new value; SteamVR itself only needs telling while it is running.
            if (_live is { SteamVrRunning: true })
            {
                try { await _live.ApplyAsync(mbps); }
                catch (Exception error) when (error is IOException or InvalidOperationException)
                {
                    throw new InvalidOperationException($"The bitrate was saved, but SteamVR's live setting could not be changed ({error.Message}). It applies the next time SteamVR starts.", error);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            Publish(Map(await _codec.ReadStatusAsync(CancellationToken.None)));
            throw;
        }
        finally { _gate.Release(); }
    }

    public void SavePassword(string password) => _vault.Save(password);
    public void ForgetPassword() => _vault.Delete();

    /// <summary>At launch: attach to a live kept session, or resume one that was armed when the PC or app last stopped.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var status = await _codec.ReadStatusAsync(cancellationToken);
        if (status.Live)
        {
            _wasLive = status.Keep;
            Publish(Map(status));
            return;
        }
        if (!_preferences.KeepH264) { Publish(Map(status)); return; }
        if (!status.CanStart) { Publish(new(TrayState.Error, status.Message, TargetMbps)); return; }
        string? password;
        try { password = _vault.Load(); }
        catch (System.ComponentModel.Win32Exception error) { Publish(new(TrayState.Error, "The stored Frame password could not be read: " + error.Message, TargetMbps)); return; }
        if (string.IsNullOrEmpty(password))
        {
            _preferences.KeepH264 = false;
            await _save();
            Publish(new(TrayState.Disarmed, "Disarmed: no Frame password is stored, so H.264 could not resume. Arm it again.", TargetMbps));
            return;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Publish(new(TrayState.Arming, "Resuming H.264…", TargetMbps));
            var result = await _codec.StartAsync(Request(), _preferences, password, new Progress<CodecServiceStatus>(s => Publish(Map(s))), cancellationToken, resume: true);
            _wasLive = true;
            Publish(Map(result));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Publish(new(TrayState.Error, "H.264 could not resume: " + error.Message, TargetMbps));
        }
        finally { password = null; _gate.Release(); }
    }

    /// <summary>Poll the supervisor's status file; ignored while an arm/disarm is in progress.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_gate.CurrentCount == 0) return;
        var status = await _codec.ReadStatusAsync(cancellationToken);
        if (_gate.CurrentCount == 0) return;
        if (_wasLive && !status.Live && _preferences.KeepH264)
        {
            // The supervisor ended on its own (it restores stock when it cannot keep H.264 applied).
            _wasLive = false;
            _preferences.KeepH264 = false;
            await _save();
            Publish(new(TrayState.Disarmed, "H.264 was released: " + (status.Message is { Length: > 0 } m ? m : "the codec service ended."), TargetMbps));
            return;
        }
        if (status.Live) _wasLive = status.Keep;
        Publish(Map(status));
        await FollowSteamVrAsync(status, cancellationToken);
    }

    /// <summary>
    /// Arms H.264. With SteamVR closed it waits ("armed, waiting for SteamVR") and never launches it; with SteamVR running it
    /// restarts SteamVR once, after confirmRestart (null means already agreed) says yes.
    /// </summary>
    public async Task<bool> ArmAsync(Func<Task<string?>> requestPassword, Func<BitratePatchStatus, Task<bool>> confirmPatch,
        Func<Task<bool>>? confirmRestart = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? typed = null;
        try
        {
            var request = Request();
            request.Validate();
            var status = await _codec.ReadStatusAsync(cancellationToken);
            var steamVrRunning = _live?.SteamVrRunning ?? false;
            var alreadyApplied = status.Live && status.Keep && status.ActiveCodec == "H.264" && status.TargetMbps == request.TargetMbps && status.State is "ready" or "waiting";
            if (!alreadyApplied && steamVrRunning && confirmRestart is not null && !await confirmRestart())
            {
                Publish(Map(status));
                return false;
            }
            Publish(new(TrayState.Arming, "Arming H.264…", TargetMbps));
            await EnsureBitratePatchAsync(request.TargetMbps, confirmPatch);
            var progress = new Progress<CodecServiceStatus>(s => Publish(Map(s)));
            CodecServiceStatus result;
            if (alreadyApplied) result = status;
            else
            {
                if (status.Live)
                {
                    // A session that cannot take this change live: end it, then start a fresh one.
                    await _codec.CloseAsync(progress, cancellationToken);
                    steamVrRunning = _live?.SteamVrRunning ?? false;
                }
                var password = _vault.Load();
                if (string.IsNullOrEmpty(password))
                {
                    typed = await requestPassword();
                    if (string.IsNullOrEmpty(typed)) { Publish(Map(await _codec.ReadStatusAsync(cancellationToken))); return false; }
                    password = typed;
                }
                result = await _codec.StartAsync(request, _preferences, password, progress, cancellationToken, resume: !steamVrRunning);
            }
            // The password is stored only after the Frame accepted it.
            if (typed is not null) _vault.Save(typed);
            _preferences.KeepH264 = true;
            await _save();
            _wasLive = true;
            Publish(Map(result));
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            Publish(new(TrayState.Error, error.Message, TargetMbps));
            throw;
        }
        finally { typed = null; _gate.Release(); }
    }

    public async Task DisarmAsync(Func<Task<string?>> requestPassword, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Publish(new(TrayState.Disarming, "Restoring SteamVR's own codec and bitrate…", TargetMbps));
            var progress = new Progress<CodecServiceStatus>(s => Publish(new(TrayState.Disarming, s.Message, TargetMbps)));
            var status = await _codec.ReadStatusAsync(cancellationToken);
            if (status.Live) await _codec.CloseAsync(progress, cancellationToken);
            else if (HostSettingsPending())
            {
                // No supervisor is running but it left its settings applied (for example after a crash or reboot):
                // a short restore session puts the saved originals back.
                var password = _vault.Load();
                if (string.IsNullOrEmpty(password)) password = await requestPassword();
                if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("The Frame password is needed to restore the stock settings.");
                await _codec.RestoreLeftoversAsync(_preferences, password, cancellationToken);
                password = null;
            }
            _preferences.KeepH264 = false;
            await _save();
            _wasLive = false;
            Publish(new(TrayState.Disarmed, "Disarmed. SteamVR uses its own codec and bitrate.", TargetMbps));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            Publish(new(TrayState.Error, "Could not disarm: " + error.Message, TargetMbps));
            throw;
        }
        finally { _gate.Release(); }
    }

    private CodecStartRequest Request() => new(TargetMbps, _preferences.HookRateControl, _preferences.HookPreset, _preferences.HookVbvFrames);

    /// <summary>Windows is ending the session: ask a live supervisor to restore now (it also notices the tray disappearing).</summary>
    public void RequestCloseNoWait() => _codec.RequestCloseNoWait();

    private async Task EnsureBitratePatchAsync(int mbps, Func<BitratePatchStatus, Task<bool>> confirmPatch)
    {
        if (mbps <= PatchFreeMaxMbps) return;
        var state = await _patch.InspectAsync();
        // An unrecognised driver cannot be patched; the request goes ahead and SteamVR applies its own ceiling.
        if (state.IsPatched || !state.IsSupported) return;
        if (!await confirmPatch(state)) throw new InvalidOperationException($"Bitrates above {PatchFreeMaxMbps} Mbps need the bitrate patch. Choose {PatchFreeMaxMbps} or lower, or allow the patch.");
        if (!state.CanApply) throw new InvalidOperationException(state.VrServerRunning
            ? "Close SteamVR, then arm again so the bitrate patch can be installed."
            : state.Message);
        var applied = await _patch.ApplyAsync();
        if (!applied.IsPatched) throw new InvalidOperationException("The bitrate patch could not be verified after installing it. " + applied.Message);
    }

    /// <summary>True when the supervisor's saved record says it changed SteamVR's streaming settings and did not put them back.</summary>
    private bool HostSettingsPending()
    {
        try
        {
            var path = Path.Combine(_codec.SessionDirectory, "host-original.json");
            if (!File.Exists(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("applied", out var applied) && applied.ValueKind == JsonValueKind.Object && applied.EnumerateObject().Any();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    private TraySnapshot Map(CodecServiceStatus status)
    {
        var target = status.TargetMbps is >= MinimumMbps and <= MaximumMbps ? status.TargetMbps : TargetMbps;
        if (!status.Live)
            return new(status.State == "error" ? TrayState.Error : TrayState.Disarmed,
                status.State == "error" ? status.Message : "Disarmed. SteamVR uses its own codec and bitrate.", TargetMbps);
        if (!status.Keep && status.ActiveCodec != "H.264")
            return new(TrayState.Disarmed, $"A codec session is running ({status.ActiveCodec}); H.264 is not armed.", TargetMbps);
        return status.State switch
        {
            "starting" or "switching" => new(TrayState.Arming, status.Message, target),
            "waiting" => new(TrayState.Waiting, $"Armed: H.264 {target} Mbps, waiting for SteamVR to start.", target),
            "ready" => new(TrayState.Armed, $"Armed: H.264 {target} Mbps." + (status.AutomaticBandwidth ? " SteamVR's Automatic bandwidth is on and may lower it." : ""), target),
            "error" => new(TrayState.Error, status.Message, target),
            _ => new(TrayState.Disarmed, "Disarmed. SteamVR uses its own codec and bitrate.", TargetMbps)
        };
    }

    private void Publish(TraySnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(snapshot);
    }
}
