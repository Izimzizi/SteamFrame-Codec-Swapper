using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteamLinkVRSettings.Frame;

public sealed record CodecSwitchRequest(StreamCodec Codec, int TargetMbps = 100, int Fps = 90)
{
    public string CodecName => Codec switch { StreamCodec.Hevc => "HEVC", StreamCodec.Av1 => "AV1", StreamCodec.H264 => "H.264", _ => throw new ArgumentOutOfRangeException(nameof(Codec)) };
    public static int MaximumMbpsFor(StreamCodec codec) => codec switch
    {
        StreamCodec.Av1 => 200, StreamCodec.H264 or StreamCodec.Hevc => 500,
        _ => throw new ArgumentOutOfRangeException(nameof(codec))
    };
    public void Validate()
    {
        _ = CodecName;
        if (TargetMbps < 25 || TargetMbps > MaximumMbpsFor(Codec)) throw new ArgumentOutOfRangeException(nameof(TargetMbps), $"Choose a {CodecName} target from 25 to {MaximumMbpsFor(Codec)} Mbps.");
        if (Fps is not (90 or 120)) throw new ArgumentOutOfRangeException(nameof(Fps), "Choose 90 or 120 Hz.");
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
    [JsonPropertyName("maximum_h264_mbps")] public int MaximumH264Mbps { get; init; } = 200;
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
        _sessionDirectory = Path.GetFullPath(sessionDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamLinkVRSettings", "CodecSession"));
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
                status.MaximumH264Mbps is < 25 or > 500 ||
                status.State is not ("starting" or "switching" or "ready" or "error" or "closed") ||
                status.ActiveCodec is not ("HEVC" or "AV1" or "H.264" or "Unknown"))
                throw new InvalidDataException("Codec status is incompatible.");
            var created = _ownerCreation(status.OwnerPid);
            if (created is null) return status with { Live = false, CanStart = true, State = "closed", ActiveCodec = "Unknown", Message = "The previous codec service has exited. Start a new session to switch codecs." };
            if (Math.Abs(created.Value - status.OwnerCreatedEpoch) > 2)
                return status with { Live = false, CanStart = false, ActiveCodec = "Unknown", Message = "Codec service ownership could not be verified. The saved status belongs to a different process." };
            var age = _now() - status.UpdatedEpoch;
            if (age is > 6 or < -2)
                return status with { Live = false, CanStart = false, ActiveCodec = "Unknown", Message = "Codec service heartbeat is stale. Wait for recovery before starting another session." };
            return status with { Live = true, CanStart = false, Message = SafeMessage(status.Message) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new() { State = "error", Message = "Codec service status is unavailable or incompatible. No switch was sent." };
        }
    }

    public async Task<CodecServiceStatus> SwitchAsync(CodecSwitchRequest request, FramePreferences preferences,
        string? password, IProgress<CodecServiceStatus>? progress = null, CancellationToken cancellationToken = default)
    {
        request.Validate();
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = ReadStatus();
            if (status.Busy) throw new InvalidOperationException("A codec switch is already running. Wait for it to finish.");
            if (status.Live)
            {
                if (request.Codec == StreamCodec.H264 && request.TargetMbps > status.MaximumH264Mbps)
                    throw new InvalidOperationException($"The running codec companion only supports H.264 up to {status.MaximumH264Mbps} Mbps. Select HEVC and apply first, then start H.264 again with the updated runtime.");
                return await SendAsync("switch", request, status, progress, cancellationToken).ConfigureAwait(false);
            }
            if (!status.CanStart) throw new InvalidOperationException(status.Message);
            ValidateConnection(preferences);
            if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("Enter the Frame user password to start the codec service.");
            if (password.Length > 128 || password.Contains('\r') || password.Contains('\n')) throw new ArgumentException("Use the Frame user password in the private password field.");
            Directory.CreateDirectory(_sessionDirectory);
            var configPath = Path.Combine(_sessionDirectory, $"config-{Guid.NewGuid():D}.json");
            await WriteAtomicAsync(configPath, new { protocol_version = 1, initial_codec = request.CodecName,
                steam_root = _steamPaths.SteamRoot, steamvr_root = _steamPaths.SteamVrRoot,
                ssh_host = preferences.SshHost, ssh_key = Path.GetFullPath(preferences.SshKey), ssh_known_hosts = Path.GetFullPath(preferences.SshKnownHosts),
                target_mbps = request.TargetMbps, fps = request.Fps }, cancellationToken).ConfigureAwait(false);
            var info = CreateStartInfo(_runtimeDirectory, _sessionDirectory, configPath);
            _ownedProcess?.Dispose();
            _ownedProcess = Process.Start(info) ?? throw new InvalidOperationException("The codec service did not start.");
            // Drain without retaining or showing raw subprocess output. status.json is the nonsecret contract.
            _ = DrainAsync(_ownedProcess.StandardOutput);
            _ = DrainAsync(_ownedProcess.StandardError);
            try
            {
                await _ownedProcess.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { password })).ConfigureAwait(false);
                await _ownedProcess.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) { throw new InvalidOperationException("Codec authentication could not be submitted. No password was saved."); }
            finally { _ownedProcess.StandardInput.Close(); }
            password = null;
            return await WaitAsync(null, _ownedProcess.Id, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    public async Task<CodecServiceStatus> CloseAsync(IProgress<CodecServiceStatus>? progress = null, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = ReadStatus();
            if (!status.Live || status.Busy) throw new InvalidOperationException(status.Busy ? "Wait for the current codec operation before restoring HEVC." : status.Message);
            return await SendAsync("close", new(StreamCodec.Hevc, status.TargetMbps is >= 25 and <= 500 ? status.TargetMbps : 100,
                status.Fps is 90 or 120 ? status.Fps : 90), status, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private async Task<CodecServiceStatus> SendAsync(string action, CodecSwitchRequest request, CodecServiceStatus owner,
        IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");
        var directory = Path.Combine(_sessionDirectory, "requests");
        Directory.CreateDirectory(directory);
        // Revalidate ownership after creating the directory and before publishing the request.
        var current = ReadStatus();
        if (!current.Live || current.OwnerPid != owner.OwnerPid || current.OwnerCreatedEpoch != owner.OwnerCreatedEpoch || current.Busy)
            throw new InvalidOperationException("Codec service ownership or state changed before the switch. No request was sent.");
        await WriteAtomicAsync(Path.Combine(directory, $"request-{id}.json"), new { protocol_version = 1, id, action,
            codec = request.CodecName, target_mbps = request.TargetMbps, fps = request.Fps }, cancellationToken).ConfigureAwait(false);
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
                if (operation is null && status.State == "ready") return status;
                if (operation is null && status.State == "error") throw new InvalidOperationException(SafeMessage(status.Message));
                if (operation is not null && status.OperationId == operation && status.LastResult is { } result && !status.Busy)
                {
                    if (!result.Ok) throw new InvalidOperationException(SafeMessage(result.Error ?? status.Message));
                    return status;
                }
            }
            else if (_ownerCreation(ownerPid) is null)
                throw new InvalidOperationException("The codec service exited before confirming the operation. Check the current codec in the dashboard.");
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
        if (preferences.SshHost != "10.35.78.1") throw new InvalidOperationException("Configure the Frame's trusted dedicated dongle SSH connection (10.35.78.1) under Connection setup first.");
        if (!File.Exists(preferences.SshKey) || !File.Exists(preferences.SshKnownHosts)) throw new InvalidOperationException("The trusted SteamOS Devkit key and known-hosts files are required. Pair the Frame before switching codecs.");
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
