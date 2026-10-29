using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteamFrame.Core;

/// <summary>What to start: H.264 at a bitrate. The two encoder options are an experiment and default to the hook's usual VBR / preset P2.</summary>
public sealed record CodecStartRequest(int TargetMbps = 100, string RateControl = "vbr", int Preset = 2, int VbvFrames = 2)
{
    public const int MinimumMbps = 25, MaximumMbps = 500;
    public void Validate()
    {
        if (TargetMbps < MinimumMbps || TargetMbps > MaximumMbps) throw new ArgumentOutOfRangeException(nameof(TargetMbps), $"Choose a target from {MinimumMbps} to {MaximumMbps} Mbps.");
        if (RateControl is not ("vbr" or "cbr")) throw new ArgumentOutOfRangeException(nameof(RateControl), "Rate control is vbr or cbr.");
        if (Preset is < 1 or > 7) throw new ArgumentOutOfRangeException(nameof(Preset), "The preset is P1 to P7.");
        if (VbvFrames is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(VbvFrames), "The VBV buffer is 1 to 16 frames.");
    }
}

public sealed record CodecServiceStatus
{
    [JsonPropertyName("protocol_version")] public int ProtocolVersion { get; init; }
    [JsonPropertyName("owner_pid")] public int OwnerPid { get; init; }
    [JsonPropertyName("owner_created_epoch")] public double OwnerCreatedEpoch { get; init; }
    [JsonPropertyName("updated_epoch")] public double UpdatedEpoch { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = "closed";
    [JsonPropertyName("active_codec")] public string ActiveCodec { get; init; } = "Unknown";
    [JsonPropertyName("message")] public string Message { get; init; } = "Codec service has not started.";
    [JsonPropertyName("operation_id")] public string OperationId { get; init; } = "";
    [JsonPropertyName("last_result")] public CodecOperationResult? LastResult { get; init; }
    [JsonPropertyName("target_mbps")] public int TargetMbps { get; init; }
    [JsonPropertyName("fps")] public int Fps { get; init; }
    /// <summary>H.264 stays applied across SteamVR restarts until it is closed.</summary>
    [JsonPropertyName("keep")] public bool Keep { get; init; }
    /// <summary>SteamVR's Automatic bandwidth is on (the user's choice, left alone); it may lower the bitrate below the target.</summary>
    [JsonPropertyName("automatic_bandwidth")] public bool AutomaticBandwidth { get; init; }
    /// <summary>The bitrate can change without restarting SteamVR (see <see cref="CodecController.SetRateAsync"/>).</summary>
    [JsonPropertyName("rate_live")] public bool RateLive { get; init; }
    /// <summary>The running SteamVR can switch between H.264 and its own HEVC without a restart (see <see cref="CodecController.SetModeAsync"/>).</summary>
    [JsonPropertyName("mode_live")] public bool ModeLive { get; init; }
    [JsonIgnore] public bool Live { get; init; }
    [JsonIgnore] public bool CanStart { get; init; }
    [JsonIgnore] public bool Busy => Live && State is "starting" or "switching";
    public static CodecServiceStatus NotStarted { get; } = new() { CanStart = true };
}

public sealed record CodecOperationResult
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>Nonsecret file IPC; the administrator password goes only to the owned process's stdin.</summary>
public sealed class CodecController
{
    private readonly string _sessionDirectory;
    private readonly string _runtimeDirectory;
    private readonly Func<int, double?> _ownerCreation;
    private readonly Func<double> _now;
    private readonly SteamPaths _steamPaths;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private Process? _ownedProcess;
    public string SessionDirectory => _sessionDirectory;

    public CodecController(string? sessionDirectory = null, string? runtimeDirectory = null,
        Func<int, double?>? ownerCreation = null, Func<double>? now = null, SteamPaths? steamPaths = null)
    {
        _sessionDirectory = Path.GetFullPath(sessionDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamFrameTray", "CodecSession"));
        _runtimeDirectory = Path.GetFullPath(runtimeDirectory ?? Path.Combine(AppContext.BaseDirectory, "CodecLab"));
        _ownerCreation = ownerCreation ?? ProcessCreatedEpoch;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        _steamPaths = steamPaths ?? SteamPaths.Current;
    }

    public Task<CodecServiceStatus> ReadStatusAsync(CancellationToken cancellationToken = default) => Task.Run(() => ReadStatus(), cancellationToken);

    private CodecServiceStatus ReadStatus()
    {
        var path = Path.Combine(_sessionDirectory, "status.json");
        if (!File.Exists(path)) return _ownedProcess is { HasExited: false }
            ? new() { State = "starting", Message = "Starting the codec service…" } : CodecServiceStatus.NotStarted;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 65536) throw new InvalidDataException("Codec status is too large.");
            var status = JsonSerializer.Deserialize<CodecServiceStatus>(stream) ?? throw new InvalidDataException("Codec status is empty.");
            if (status.ProtocolVersion != 1 || status.OwnerPid <= 0 || !double.IsFinite(status.OwnerCreatedEpoch) || !double.IsFinite(status.UpdatedEpoch) ||
                status.State is not ("starting" or "switching" or "ready" or "waiting" or "error" or "closed") ||
                status.ActiveCodec is not ("HEVC" or "H.264" or "Unknown"))
                throw new InvalidDataException("Codec status is incompatible.");
            var created = _ownerCreation(status.OwnerPid);
            if (created is null) return status with { Live = false, CanStart = true, State = "closed", ActiveCodec = "Unknown", Message = "The previous codec service has exited. Start a new session to switch codecs." };
            if (Math.Abs(created.Value - status.OwnerCreatedEpoch) > 2)
            {
                // Windows can reuse a finished supervisor's PID for an unrelated
                // app. A completed rollback with no owner lock must not leave HEVC
                // monitoring permanently stuck at "reconnecting".
                if (status.State == "closed" && !OwnerLockHeld())
                    return CodecServiceStatus.NotStarted with { Message = "The previous codec session is closed; its process ID has been reused." };
                return status with { Live = false, CanStart = false, ActiveCodec = "Unknown", Message = "Codec service ownership could not be verified. The saved status belongs to a different process." };
            }
            // A closed session has already restored HEVC; its owner is only exiting.
            // Treat it as finished so a second close or restore is never sent to it.
            if (status.State == "closed")
                return status with { Live = false, CanStart = false, Message = "The previous codec session is finishing." };
            var age = _now() - status.UpdatedEpoch;
            if (age is > 6 or < -2)
                return status with { Live = false, CanStart = false, ActiveCodec = "Unknown", Message = "Codec service heartbeat is stale. Wait for recovery before starting another session." };
            return status with { Live = true, CanStart = false, Message = SafeMessage(status.Message) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            // The supervisor holds owner.lock for its whole lifetime. Without a holder, an
            // unreadable status file is stale and must not block the app indefinitely.
            if (!OwnerLockHeld()) return CodecServiceStatus.NotStarted with { Message = "The previous codec status was unreadable; no codec service is running." };
            return new() { State = "error", Message = "Codec service status is unavailable or incompatible. No switch was sent." };
        }
    }

    private bool OwnerLockHeld()
    {
        var path = Path.Combine(_sessionDirectory, "owner.lock");
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            stream.Lock(0, 1); stream.Unlock(0, 1);
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Starts the supervisor for this tray. resume: SteamVR is closed (or the app restarted), so nothing is restarted and
    /// H.264 is applied when SteamVR next starts; otherwise a running SteamVR is restarted once with H.264.
    /// </summary>
    public async Task<CodecServiceStatus> StartAsync(CodecStartRequest request, FramePreferences preferences,
        string? password, IProgress<CodecServiceStatus>? progress = null, CancellationToken cancellationToken = default, bool resume = false)
    {
        request.Validate();
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = ReadStatus();
            if (status.Busy) throw new InvalidOperationException("A codec operation is already running. Wait for it to finish.");
            if (status.Live) throw new InvalidOperationException("A codec session is already running. Close it first.");
            if (!status.CanStart) throw new InvalidOperationException(status.Message);
            var process = await LaunchAsync(new { protocol_version = 1, steam_root = _steamPaths.SteamRoot, steamvr_root = _steamPaths.SteamVrRoot,
                ssh_host = preferences.SshHost, ssh_key = Path.GetFullPath(preferences.SshKey), ssh_known_hosts = Path.GetFullPath(preferences.SshKnownHosts),
                target_mbps = request.TargetMbps, rate_control = request.RateControl, preset = request.Preset, vbv_frames = request.VbvFrames, resume, tray_pid = Environment.ProcessId },
                preferences, password, cancellationToken).ConfigureAwait(false);
            password = null;
            return await WaitAsync(null, process.Id, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    /// <summary>
    /// A session ended without restoring SteamVR's streaming settings (a crash or reboot): runs a short session that puts the
    /// saved originals back and unmounts the patched client, and waits for it to finish.
    /// </summary>
    public async Task RestoreLeftoversAsync(FramePreferences preferences, string? password, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = ReadStatus();
            if (status.Live || !status.CanStart) throw new InvalidOperationException(status.Live ? "A codec session is already running." : status.Message);
            var process = await LaunchAsync(new { protocol_version = 1, steam_root = _steamPaths.SteamRoot, steamvr_root = _steamPaths.SteamVrRoot,
                ssh_host = preferences.SshHost, ssh_key = Path.GetFullPath(preferences.SshKey), ssh_known_hosts = Path.GetFullPath(preferences.SshKnownHosts),
                target_mbps = 100, restore_only = true, tray_pid = Environment.ProcessId },
                preferences, password, cancellationToken).ConfigureAwait(false);
            password = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("The restore has not finished. Check the session folder before trying again."); }
            var after = ReadStatus();
            if (after.LastResult is { Ok: false } failed) throw new InvalidOperationException(SafeMessage(failed.Error ?? after.Message));
        }
        finally { _operations.Release(); }
    }

    private async Task<Process> LaunchAsync(object config, FramePreferences preferences, string? password, CancellationToken cancellationToken)
    {
        ValidateConnection(preferences);
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("Enter the Frame user password to start the codec service.");
        if (password.Length > 128 || password.Contains('\r') || password.Contains('\n')) throw new ArgumentException("Use the Frame user password in the private password field.");
        Directory.CreateDirectory(_sessionDirectory);
        var configPath = Path.Combine(_sessionDirectory, $"config-{Guid.NewGuid():D}.json");
        await WriteAtomicAsync(configPath, config, cancellationToken).ConfigureAwait(false);
        var info = CreateStartInfo(_runtimeDirectory, _sessionDirectory, configPath);
        _ownedProcess?.Dispose();
        var process = _ownedProcess = Process.Start(info) ?? throw new InvalidOperationException("The codec service did not start.");
        // Drain without retaining or showing raw subprocess output. status.json is the nonsecret contract.
        _ = DrainAsync(process.StandardOutput);
        _ = DrainAsync(process.StandardError);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { password })).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) { throw new InvalidOperationException("Codec authentication could not be submitted. No password was saved."); }
        finally { process.StandardInput.Close(); }
        return process;
    }

    public async Task<CodecServiceStatus> CloseAsync(IProgress<CodecServiceStatus>? progress = null, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = ReadStatus();
            if (!status.Live || status.Busy) throw new InvalidOperationException(status.Busy ? "Wait for the current codec operation before restoring SteamVR's codec." : status.Message);
            return await SendAsync("close", 0, status, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    /// <summary>
    /// Asks a live session to close without waiting for the answer (Windows is ending the session). The supervisor also
    /// notices the tray process disappearing, so this only makes the restore start sooner.
    /// </summary>
    public void RequestCloseNoWait()
    {
        try
        {
            var status = ReadStatus();
            if (!status.Live) return;
            var id = Guid.NewGuid().ToString("D");
            var directory = Path.Combine(_sessionDirectory, "requests");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"request-{id}.json"), JsonSerializer.Serialize(new { protocol_version = 1, id, action = "close" }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Changes only the H.264 bitrate of a live session; the supervisor refuses it when a restart would be needed.</summary>
    public async Task<CodecServiceStatus> SetRateAsync(int targetMbps, IProgress<CodecServiceStatus>? progress = null, CancellationToken cancellationToken = default)
    {
        if (targetMbps is < 25 or > 500) throw new ArgumentOutOfRangeException(nameof(targetMbps), "Choose 25-500 Mbps.");
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = ReadStatus();
            if (!status.Live || status.Busy) throw new InvalidOperationException(status.Busy ? "Wait for the current codec operation to finish." : status.Message);
            if (!status.RateLive) throw new InvalidOperationException("This session cannot change its bitrate without restarting SteamVR.");
            return await SendAsync("rate", targetMbps, status, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    /// <summary>Queues new encoder options (rate control, preset, VBV) on the resident hook; the encoder SteamVR builds next uses them.</summary>
    public async Task<CodecServiceStatus> SetEncoderAsync(string rateControl, int preset, int vbvFrames, IProgress<CodecServiceStatus>? progress = null, CancellationToken cancellationToken = default)
    {
        new CodecStartRequest(RateControl: rateControl, Preset: preset, VbvFrames: vbvFrames).Validate();
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = ReadStatus();
            if (!status.Live || status.Busy) throw new InvalidOperationException(status.Busy ? "Wait for the current codec operation to finish." : status.Message);
            if (!status.RateLive) throw new InvalidOperationException("This session cannot change its encoder without restarting SteamVR.");
            return await SendAsync("encoder", 0, status, progress, cancellationToken, new CodecStartRequest(RateControl: rateControl, Preset: preset, VbvFrames: vbvFrames)).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    /// <summary>Switches the running SteamVR between H.264 ("h264") and its own HEVC ("stock") without restarting it: the stream stops, the hook and the Frame's client flip together, the stream starts again.</summary>
    public async Task<CodecServiceStatus> SetModeAsync(string mode, IProgress<CodecServiceStatus>? progress = null, CancellationToken cancellationToken = default)
    {
        if (mode is not ("h264" or "stock")) throw new ArgumentOutOfRangeException(nameof(mode), "Mode is h264 or stock.");
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = ReadStatus();
            if (!status.Live || status.Busy) throw new InvalidOperationException(status.Busy ? "Wait for the current codec operation to finish." : status.Message);
            if (!status.ModeLive) throw new InvalidOperationException("SteamVR was started without the live codec hook; arm H.264 and let SteamVR start (or restart it) first.");
            return await SendAsync("mode", 0, status, progress, cancellationToken, mode: mode).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private async Task<CodecServiceStatus> SendAsync(string action, int targetMbps, CodecServiceStatus owner,
        IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken, CodecStartRequest? encoder = null, string? mode = null)
    {
        var id = Guid.NewGuid().ToString("D");
        var directory = Path.Combine(_sessionDirectory, "requests");
        Directory.CreateDirectory(directory);
        // Revalidate ownership after creating the directory and before publishing the request.
        var current = ReadStatus();
        if (!current.Live || current.OwnerPid != owner.OwnerPid || current.OwnerCreatedEpoch != owner.OwnerCreatedEpoch || current.Busy)
            throw new InvalidOperationException("Codec service ownership or state changed before the switch. No request was sent.");
        object body = action == "rate" ? new { protocol_version = 1, id, action, target_mbps = targetMbps }
            : action == "mode" ? new { protocol_version = 1, id, action, mode }
            : action == "encoder" ? new { protocol_version = 1, id, action, rate_control = encoder!.RateControl, preset = encoder.Preset, vbv_frames = encoder.VbvFrames }
            : new { protocol_version = 1, id, action };
        await WriteAtomicAsync(Path.Combine(directory, $"request-{id}.json"), body, cancellationToken).ConfigureAwait(false);
        return await WaitAsync(id, owner.OwnerPid, progress, cancellationToken, action == "close", owner.OwnerCreatedEpoch).ConfigureAwait(false);
    }

    private async Task<CodecServiceStatus> WaitAsync(string? operation, int ownerPid, IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken,
        bool closing = false, double ownerCreated = 0)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = ReadStatus();
            progress?.Report(status);
            // Closing can publish its successful rollback just before the owner exits.
            if (closing && status.OwnerPid == ownerPid && status.OwnerCreatedEpoch == ownerCreated &&
                status.OperationId == operation && status.State == "closed" && status.LastResult is { Ok: true }) return status;
            if (status.Live && status.OwnerPid == ownerPid)
            {
                if (operation is null && status.State is "ready" or "waiting") return status;
                if (operation is null && status.State == "error") throw new InvalidOperationException(SafeMessage(status.Message));
                if (operation is not null && status.OperationId == operation && status.LastResult is { } result && !status.Busy)
                {
                    if (!result.Ok) throw new InvalidOperationException(SafeMessage(result.Error ?? status.Message));
                    return status;
                }
            }
            else if (_ownerCreation(ownerPid) is null)
                throw new InvalidOperationException("The codec service exited before confirming the operation. Check the tray status.");
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("The codec switch has not finished. Check the service status before trying again; its recovery may still be running.");
    }

    internal static ProcessStartInfo CreateStartInfo(string runtimeDirectory, string sessionDirectory, string configPath)
    {
        var python = Path.Combine(runtimeDirectory, "runtime", "python.exe");
        var supervisor = Path.Combine(runtimeDirectory, "codec_supervisor.py");
        if (!File.Exists(python) || !File.Exists(supervisor)) throw new FileNotFoundException("The paired codec runtime is missing. Install the codec-enabled app package.");
        var info = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runtimeDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(supervisor);
        info.ArgumentList.Add("--session-dir"); info.ArgumentList.Add(sessionDirectory);
        info.ArgumentList.Add("--config"); info.ArgumentList.Add(configPath);
        return info;
    }

    internal static void ValidateConnection(FramePreferences preferences)
    {
        if (!FrameAddress.IsValid(preferences.SshHost)) throw new InvalidOperationException("Set the Frame's address (its hostname, normally frame, or an IP address) first.");
        if (!File.Exists(preferences.SshKey) || !File.Exists(preferences.SshKnownHosts)) throw new InvalidOperationException("The SSH key and the Frame's pinned host key are required. Run the setup check first.");
    }

    private static async Task WriteAtomicAsync(string path, object value, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { var buffer = new char[1024]; while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
    private static double? ProcessCreatedEpoch(int id)
    {
        try { using var process = Process.GetProcessById(id); return process.HasExited ? null : new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0; }
        catch (ArgumentException) { return null; }
    }
    private static string SafeMessage(string message) => Regex.Replace(message.Length > 600 ? message[..600] : message, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "");
}
