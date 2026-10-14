using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SteamLinkVRSettings.Frame;

/// <summary>
/// Explicitly starts a layout-guarded helper in vrserver. Polling never injects, starts
/// SteamVR, changes networking, or unloads live trampolines. No process arguments are read.
/// </summary>
public sealed partial class EncoderProbe
{
    private const string HelperName = "SteamFrameEncoderProbe.dll";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;
    private EncoderSnapshot _lastSnapshot = new();
    private bool _sessionFaulted;
    private string? _inspectionIdentity;
    private DateTime _inspectionExpiry;
    private bool _inspectionSupported;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint InspectDriverFile([MarshalAs(UnmanagedType.LPWStr)] string path);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint LocalProbeVersion(nint unused);
    private bool _protocolVerified;
    private sealed record Session(int ProcessId, ulong CreationTime, string HelperPath,
        Dictionary<string, uint> Exports, string StatusPath);
    private sealed record Module(string Name, string Path, nint Base, int Size);

    public Task<EncoderSnapshot> GetSnapshotAsync() => RunAsync(() =>
    {
        using var process = DiscoverProcess();
        if (process is null) { ResetAv1State(); return new() { WaitingForDriver = true, Message = "SteamVR is not running." }; }
        var modules = ReadModules(process);
        var driver = modules.FirstOrDefault(m => m.Name.Equals("driver_vrlink.dll", StringComparison.OrdinalIgnoreCase));
        if (driver is null) { ResetAv1State(); return new() { ProcessId = process.Id, WaitingForDriver = true, Message = "Steam Link VR driver is not loaded." }; }
        var av1 = modules.FirstOrDefault(m => m.Name.Equals("SteamFrameAV1Backend.dll", StringComparison.OrdinalIgnoreCase));
        if (av1 is not null) return ReadAv1Snapshot(process, av1);
        ResetAv1State();
        if (_session is null || _session.ProcessId != process.Id || _session.CreationTime != ProcessCreationTime(process))
        {
            _session = null;
            _sessionFaulted = false;
            _protocolVerified = false;
            var supported = IsSupportedDriver(driver.Path);
            var loaded = modules.FirstOrDefault(m => m.Name.Equals(HelperName, StringComparison.OrdinalIgnoreCase));
            var source = Path.Combine(AppContext.BaseDirectory, "Native", HelperName);
            // Reuse monitoring the user already started. Reading never installs or restarts hooks.
            if (supported && loaded is not null && File.Exists(source) && File.Exists(loaded.Path) &&
                FileHash(source).Equals(FileHash(loaded.Path), StringComparison.OrdinalIgnoreCase))
            {
                var exports = ReadExports(loaded.Path);
                var creation = ProcessCreationTime(process);
                using var handle = OpenTarget(process.Id);
                ValidateIdentity(handle, creation);
                var cache = Path.Combine(AppContext.BaseDirectory, "Native", "Cache", FileHash(source));
                Directory.CreateDirectory(cache);
                // Retain identity before starting a remote operation. If it times
                // out, subsequent polls must not launch additional remote threads.
                _session = new(process.Id, creation, loaded.Path, exports,
                    Path.Combine(cache, $"status-{process.Id}-{creation}-{Guid.NewGuid():N}.json"));
                if (Invoke(handle, Address(loaded, exports, "GetProbeVersion"), 0) == 1)
                {
                    _protocolVerified = true;
                    return ReadSnapshot(process, modules, _session);
                }
                return new() { ProcessId = process.Id, Supported = true, RequiresSteamVrRestart = true,
                    Message = "The loaded encoder helper protocol was not validated. Restart SteamVR before monitoring." };
            }
            if (supported && loaded is not null) return new() { ProcessId = process.Id, Supported = true, RequiresSteamVrRestart = true,
                Message = "SteamVR is still using a previous app version's encoder helper. Restart SteamVR and resume to finish the update." };
            if (supported && modules.Any(m => m.Name.Equals("SteamFrameQuality.dll", StringComparison.OrdinalIgnoreCase)))
                return new() { ProcessId = process.Id, Supported = true, RequiresSteamVrRestart = true,
                    Message = "SteamVR is still using the legacy quality helper. Restart SteamVR and resume to use the current app." };
            return new() { ProcessId = process.Id, Supported = supported, Message = supported
                ? "Select Start to monitor actual NVENC output. Other encoders provide transport statistics only."
                : "This driver has an incompatible encoder layout; monitoring was not installed." };
        }
        EnsureSessionResponsive();
        return ReadSnapshot(process, modules, _session);
    });

    public Task<EncoderSnapshot> StartAsync() => RunAsync(() =>
    {
        using var process = DiscoverProcess();
        if (process is null) return new() { WaitingForDriver = true, Message = "Start SteamVR before starting encoder monitoring." };
        if (!Environment.Is64BitProcess) return new() { ProcessId = process.Id, Message = "Encoder monitoring requires the x64 application." };
        var modules = ReadModules(process);
        var av1 = modules.FirstOrDefault(m => m.Name.Equals("SteamFrameAV1Backend.dll", StringComparison.OrdinalIgnoreCase));
        if (av1 is not null) { _av1Stopped = false; return ReadAv1Snapshot(process, av1); }
        var driver = modules.FirstOrDefault(m => m.Name.Equals("driver_vrlink.dll", StringComparison.OrdinalIgnoreCase));
        if (driver is null) return new() { ProcessId = process.Id, WaitingForDriver = true, Message = "Waiting for SteamVR's streaming driver." };
        if (!IsSupportedDriver(driver.Path)) return new() { ProcessId = process.Id,
            Message = "This driver has an incompatible encoder layout; monitoring was not installed." };
        if (modules.Any(m => m.Name.Equals("SteamFrameQuality.dll", StringComparison.OrdinalIgnoreCase)))
            return new() { ProcessId = process.Id, Supported = true, RequiresSteamVrRestart = true, Message = "Restart SteamVR to remove the older SteamFrameQuality helper before using this encoder probe." };
        var source = Path.Combine(AppContext.BaseDirectory, "Native", HelperName);
        if (!File.Exists(source)) return new() { ProcessId = process.Id, Supported = true,
            Message = "The native encoder helper is missing. Build or reinstall the application." };
        var hash = FileHash(source);
        // Keep immutable helper generations beside the application. Some SteamVR
        // processes see a different AppData view from the settings application;
        // a locally valid AppData path can therefore be missing in vrserver.
        var cache = Path.Combine(AppContext.BaseDirectory, "Native", "Cache", hash);
        Directory.CreateDirectory(cache);
        var helperPath = Path.Combine(cache, HelperName);
        if (!File.Exists(helperPath)) File.Copy(source, helperPath);
        if (!FileHash(helperPath).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The cached native helper failed its integrity check.");
        // RVAs must come from the verified immutable copy, not a source that can
        // be replaced by an app update between parsing and copying.
        var exports = ReadExports(helperPath);
        var creation = ProcessCreationTime(process);
        if (_session is not null && _session.ProcessId == process.Id && _session.CreationTime == creation)
            EnsureSessionResponsive();
        else _sessionFaulted = false;
        var alreadyLoaded = modules.FirstOrDefault(m => m.Name.Equals(HelperName, StringComparison.OrdinalIgnoreCase));
        if (alreadyLoaded is not null && !FileHash(alreadyLoaded.Path).Equals(hash, StringComparison.OrdinalIgnoreCase))
            return new() { ProcessId = process.Id, Supported = true, RequiresSteamVrRestart = true, Message = "SteamVR is still using a previous app version's encoder helper. Restart SteamVR and resume to finish the update." };
        using var handle = OpenTarget(process.Id);
        ValidateIdentity(handle, creation);
        var session = new Session(process.Id, creation, alreadyLoaded?.Path ?? helperPath, exports,
            Path.Combine(cache, $"status-{process.Id}-{creation}-{Guid.NewGuid():N}.json"));
        _session = session; // Preserve identity if a loader/export thread times out.
        _protocolVerified = false;
        if (alreadyLoaded is null)
        {
            try { LoadRemoteHelper(handle, modules, helperPath); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            {
                // A completed failed load has no outstanding remote operation;
                // retrying does not require restarting SteamVR.
                _session = null;
                throw;
            }
            process.Refresh();
            modules = ReadModules(process);
            if (!modules.Any(m => m.Name.Equals(HelperName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Windows did not retain the loaded encoder helper. Monitoring was not installed.");
        }
        var helper = FindHelper(modules, session);
        if (Invoke(handle, Address(helper, exports, "GetProbeVersion"), 0) != 1)
            return new() { ProcessId = process.Id, Supported = true, RequiresSteamVrRestart = true, Message = "The loaded encoder helper protocol is unsupported. Restart SteamVR." };
        _protocolVerified = true;
        var result = Invoke(handle, Address(helper, exports, "StartProbe"), 0);
        if (result != 0) return new() { ProcessId = process.Id, Supported = true, Message = NativeError(result) };
        _session = session;
        return ReadSnapshot(process, modules, session);
    });

    public Task<EncoderSnapshot> SetQualityAsync(bool enabled, int centerOffset, int peripheralOffset)
    {
        if (!FoveationOffsets.IsValid(centerOffset, FoveationOffsets.MaximumGaze, FoveationOffsets.MinimumGaze)) throw new ArgumentOutOfRangeException(nameof(centerOffset), "Use a gaze offset from -20 to 12.");
        if (!FoveationOffsets.IsValid(peripheralOffset, FoveationOffsets.MaximumPeripheral)) throw new ArgumentOutOfRangeException(nameof(peripheralOffset), "Use a peripheral offset from -12 to 31.");
        return RunAsync(() =>
        {
            if (_session is null) return new() { Message = "Start encoder monitoring before changing foveation quality." };
            EnsureSessionResponsive();
            using var process = Process.GetProcessById(_session.ProcessId);
            var modules = ReadModules(process);
            using var handle = OpenTarget(process.Id);
            ValidateIdentity(handle, _session.CreationTime);
            var settings = (enabled ? 0x10000u : 0u) | (uint)(centerOffset & 255) | ((uint)(peripheralOffset & 255) << 8);
            var result = Invoke(handle, Address(FindHelper(modules, _session), _session.Exports, "SetQuality"), (nint)settings);
            if (result != 0) return ReadSnapshot(process, modules, _session) with { Message = NativeError(result) };
            return ReadSnapshot(process, modules, _session);
        });
    }

    public Task<EncoderSnapshot> StopAsync() => RunAsync(() =>
    {
        if (_av1ProcessId != 0 && _session is null)
        {
            _av1Stopped = true;
            _av1Activity.Reset(); // Frames accumulated while paused are not fresh output evidence.
            return _lastAv1Snapshot with { Installed = false, Active = false, Message = "Paired codec monitoring stopped. Use the codec switcher to restore HEVC." };
        }
        if (_session is null) return new() { Message = "Encoder monitoring is stopped." };
        EnsureSessionResponsive();
        using var process = Process.GetProcessById(_session.ProcessId);
        var modules = ReadModules(process);
        using var handle = OpenTarget(process.Id);
        ValidateIdentity(handle, _session.CreationTime);
        var result = Invoke(handle, Address(FindHelper(modules, _session), _session.Exports, "StopProbe"), 0);
        if (result != 0) return ReadSnapshot(process, modules, _session) with { Message = NativeError(result) };
        return ReadSnapshot(process, modules, _session) with { Message = "Monitoring stopped and Valve's original quality map restored. The helper remains loaded until SteamVR exits." };
    });

    /// <summary>Explicit request only; polling never arms a policy or nudges stream settings.</summary>
    public Task<EncoderSnapshot> SetEncodingPolicyAsync(EncodingPolicy policy)
    {
        var packed = policy.Packed;
        return RunAsync(() =>
        {
            if (_session is null) return new() { Message = "Start encoder monitoring before changing NVENC policy." };
            EnsureSessionResponsive();
            using var process = Process.GetProcessById(_session.ProcessId);
            var modules = ReadModules(process);
            using var handle = OpenTarget(process.Id);
            ValidateIdentity(handle, _session.CreationTime);
            var result = Invoke(handle, Address(FindHelper(modules, _session), _session.Exports, "SetEncodingPolicy"), (nint)packed);
            var snapshot = ReadSnapshot(process, modules, _session);
            return result == 0 ? snapshot : snapshot with { Message = NativeError(result) };
        });
    }

    public Task<EncoderSnapshot> SetAdvancedEncodingAsync(AdvancedEncoding settings)
    {
        var bytes = settings.ToNativeBytes();
        return RunAsync(() =>
        {
            if (_session is null) return new() { Message = "Start the updated encoder monitor before changing advanced encoding." };
            EnsureSessionResponsive();
            using var process = Process.GetProcessById(_session.ProcessId);
            var modules = ReadModules(process);
            if (!_session.Exports.ContainsKey("SetAdvancedEncoding")) return ReadSnapshot(process, modules, _session) with
                { Message = "This loaded helper does not support advanced encoding. Restart SteamVR after updating the app." };
            using var handle = OpenTarget(process.Id);
            ValidateIdentity(handle, _session.CreationTime);
            var result = Invoke(handle, Address(FindHelper(modules, _session), _session.Exports, "SetAdvancedEncoding"), bytes);
            var snapshot = ReadSnapshot(process, modules, _session);
            return result == 0 ? snapshot : snapshot with { Message = NativeError(result) };
        });
    }

    private async Task<EncoderSnapshot> RunAsync(Func<EncoderSnapshot> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try { return _lastSnapshot = operation(); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or TimeoutException or OverflowException or KeyNotFoundException or BadImageFormatException or DllNotFoundException or EntryPointNotFoundException)
                {
                    if (error is TimeoutException) _sessionFaulted = true;
                    return _lastSnapshot with { Active = false, Codec = "Unknown", CodecObserved = false,
                        CodecSource = "", CodecReadFailed = true, BitDepth = null, Message = error.Message };
                }
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void EnsureSessionResponsive()
    {
        if (_sessionFaulted) throw new InvalidOperationException("The encoder helper timed out. Restart SteamVR before making another native call.");
        if (!_protocolVerified) throw new InvalidOperationException("The loaded encoder helper protocol was not validated. Restart SteamVR before making another native call.");
    }

    private static Process? DiscoverProcess()
    {
        var processes = Process.GetProcessesByName("vrserver");
        if (processes.Length == 1) return processes[0];
        foreach (var process in processes) process.Dispose();
        if (processes.Length > 1) throw new InvalidOperationException("Multiple SteamVR servers are running; close the extra instance before monitoring.");
        return null;
    }

    private static List<Module> ReadModules(Process process) => process.Modules.Cast<ProcessModule>()
        .Select(m => new Module(m.ModuleName, m.FileName, m.BaseAddress, m.ModuleMemorySize)).ToList();
    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private bool IsSupportedDriver(string path)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Native", HelperName);
        if (!File.Exists(source)) throw new FileNotFoundException("The native encoder helper is missing. Build or reinstall the application.");
        var driver = new FileInfo(path);
        var helper = new FileInfo(source);
        var identity = $"{driver.FullName}|{driver.Length}|{driver.LastWriteTimeUtc.Ticks}|{helper.Length}|{helper.LastWriteTimeUtc.Ticks}";
        if (_inspectionIdentity == identity && DateTime.UtcNow < _inspectionExpiry) return _inspectionSupported;
        // Load only our helper locally. InspectDriver parses the Valve file as data;
        // it never loads that DLL, installs hooks, or calls NVENC in this process.
        _ = ReadExports(source);
        var library = NativeLibrary.Load(source);
        try
        {
            if (Marshal.GetDelegateForFunctionPointer<LocalProbeVersion>(NativeLibrary.GetExport(library, "GetProbeVersion"))(0) != 1)
                throw new InvalidOperationException("The native encoder helper protocol is unsupported. Reinstall the application.");
            var inspect = Marshal.GetDelegateForFunctionPointer<InspectDriverFile>(NativeLibrary.GetExport(library, "InspectDriver"));
            _inspectionSupported = inspect(driver.FullName) == 0;
            _inspectionIdentity = identity;
            _inspectionExpiry = DateTime.UtcNow.AddSeconds(10);
            return _inspectionSupported;
        }
        finally { NativeLibrary.Free(library); }
    }

    private static Module FindHelper(List<Module> modules, Session session) => modules.FirstOrDefault(m =>
        m.Name.Equals(HelperName, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFullPath(m.Path).Equals(Path.GetFullPath(session.HelperPath), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("The encoder helper is no longer loaded. Select Start after SteamVR restarts.");
    private static nint Address(Module module, Dictionary<string, uint> exports, string name)
    {
        var rva = exports[name];
        if (rva >= module.Size) throw new InvalidOperationException("The helper export is outside the loaded image.");
        return module.Base + checked((int)rva);
    }

    private static EncoderSnapshot ReadSnapshot(Process process, List<Module> modules, Session session)
    {
        using var handle = OpenTarget(process.Id);
        ValidateIdentity(handle, session.CreationTime);
        var result = Invoke(handle, Address(FindHelper(modules, session), session.Exports, "WriteStatus"),
            Encoding.Unicode.GetBytes(session.StatusPath + "\0"));
        if (result != 0) return new() { ProcessId = process.Id, Supported = true, Message = NativeError(result) };
        using var document = JsonDocument.Parse(File.ReadAllText(session.StatusPath));
        var json = document.RootElement;
        if (json.GetProperty("protocol_version").GetInt32() != 1 || json.GetProperty("process_id").GetInt32() != process.Id ||
            json.GetProperty("process_creation_time").GetUInt64() != session.CreationTime)
            throw new InvalidOperationException("Discarded a stale or incompatible encoder snapshot.");
        var active = json.GetProperty("active").GetBoolean();
        var installed = json.GetProperty("installed").GetBoolean();
        var codec = StreamCodecStatus.Normalize(json.GetProperty("codec").GetString());
        return new()
        {
            ProcessId = process.Id, Supported = true, Installed = installed,
            QualitySupported = json.TryGetProperty("quality_supported", out var quality) && quality.GetBoolean(),
            Enabled = json.GetProperty("enabled").GetBoolean(), Active = active,
            CenterOffset = json.GetProperty("center_offset").GetInt32(), PeripheralOffset = json.GetProperty("peripheral_offset").GetInt32(),
            EncodedBytes = json.GetProperty("encoded_bytes").GetUInt64(), EncodedFrames = json.GetProperty("encoded_frames").GetUInt64(),
            TimestampSeconds = json.GetProperty("timestamp_seconds").GetDouble(), LastEncodedTimestampSeconds = json.GetProperty("last_encoded_timestamp_seconds").GetDouble(),
            Generation = json.GetProperty("generation").GetString() ?? "", EncodeErrors = json.GetProperty("encode_errors").GetUInt64(),
            LockErrors = json.GetProperty("lock_errors").GetUInt64(), Frames = json.GetProperty("frames").GetUInt64(),
            ValidMaps = json.GetProperty("valid_maps").GetUInt64(), ModifiedFrames = json.GetProperty("modified_frames").GetUInt64(),
            LastQualityMap = ReadQualityMap(json),
            QuantizerSamples = json.TryGetProperty("quantizer_samples", out var qpSamples) ? qpSamples.GetUInt64() : 0,
            QuantizerSum = json.TryGetProperty("quantizer_sum", out var qpSum) ? qpSum.GetUInt64() : 0,
            LastFrameQp = json.TryGetProperty("last_frame_qp", out var lastQp) ? lastQp.GetUInt32() : null,
            LastFrameBytes = json.TryGetProperty("last_frame_bytes", out var frameBytes) ? frameBytes.GetUInt32() : null,
            EncodingPolicy = ReadEncodingPolicy(json),
            AdvancedEncoding = ReadAdvancedEncoding(json), CallbackTiming = ReadCallbackTiming(json),
            LastNvencStatus = json.GetProperty("last_nvenc_status").GetUInt32(), Codec = codec,
            CodecObserved = codec != "Unknown" && json.GetProperty("encoded_frames").GetUInt64() > 0,
            CodecSource = json.TryGetProperty("codec_source", out var source) ? source.GetString() ?? ""
                : "NVENC codec metadata and encoded output",
            BitDepth = json.TryGetProperty("bit_depth", out var bitDepth) ? bitDepth.GetUInt32() : null,
            RejectedApiCalls = json.TryGetProperty("rejected_api_calls", out var rejected) ? rejected.GetUInt64() : 0,
            Message = !installed ? "Encoder monitoring is stopped." : active ? $"Measuring actual {codec} video output; transport overhead is excluded."
                : json.TryGetProperty("rejected_api_calls", out var invalidApi) && invalidApi.GetUInt64() > 0
                    ? "This stream uses an incompatible NVENC structure version; video measurements are unavailable."
                    : "Waiting for NVENC encoded frames. Other encoders provide transport statistics only."
        };
    }

    private static EncodingPolicySnapshot? ReadEncodingPolicy(JsonElement json)
    {
        if (!json.TryGetProperty("requested_encoding_policy", out var requested)) return null;
        return new()
        {
            Requested = Frame.EncodingPolicy.FromPacked(requested.GetUInt32()),
            Applied = Frame.EncodingPolicy.FromPacked(json.GetProperty("applied_encoding_policy").GetUInt32()),
            Observed = json.GetProperty("policy_observed").GetBoolean(),
            RateControl = json.GetProperty("rate_control").GetUInt32(), MultiPass = json.GetProperty("multi_pass").GetUInt32(),
            AverageBitrate = json.GetProperty("average_bitrate").GetUInt32(), MaxBitrate = json.GetProperty("max_bitrate").GetUInt32(),
            VbvSize = json.GetProperty("vbv_size").GetUInt32(), VbvDelay = json.GetProperty("vbv_delay").GetUInt32(),
            Width = json.GetProperty("encode_width").GetUInt32(), Height = json.GetProperty("encode_height").GetUInt32(),
            FrameRateNum = json.GetProperty("frame_rate_num").GetUInt32(), FrameRateDen = json.GetProperty("frame_rate_den").GetUInt32(),
            Filler = json.GetProperty("filler").GetBoolean(), Preset = json.GetProperty("preset").GetString() ?? "Unknown",
            Calls = json.GetProperty("policy_calls").GetUInt64(), Errors = json.GetProperty("policy_errors").GetUInt64(),
            RejectedCalls = json.GetProperty("rejected_policy_calls").GetUInt64(),
            NeedsNewSession = json.TryGetProperty("policy_needs_session", out var pendingSession) && pendingSession.GetBoolean(),
            Unsupported = json.TryGetProperty("policy_unsupported", out var unsupported) && unsupported.GetBoolean(),
            InitializationRejected = json.TryGetProperty("policy_initialization_rejected", out var rejectedInit) && rejectedInit.GetBoolean(),
            DynamicRateControlSupported = json.TryGetProperty("dynamic_rate_control", out var dynamicRc) ? dynamicRc.GetBoolean() : null,
            SupportedRateControlModes = json.TryGetProperty("supported_rate_control_modes", out var supportedRc) ? supportedRc.GetUInt32() : null
        };
    }

    private static bool JsonBool(JsonElement json, string name) => json.GetProperty(name).ValueKind == JsonValueKind.Number
        ? json.GetProperty(name).GetUInt32() != 0 : json.GetProperty(name).GetBoolean();
    private static AdvancedEncodingSnapshot? ReadAdvancedEncoding(JsonElement json)
    {
        if (!json.TryGetProperty("requested_advanced", out var requested) || !json.TryGetProperty("effective", out var effective)) return null;
        var flags = (AdvancedEncodingFlags)requested.GetProperty("flags").GetUInt32();
        bool Has(AdvancedEncodingFlags flag) => flags.HasFlag(flag);
        return new()
        {
            Supported = json.TryGetProperty("advanced_supported", out _) && JsonBool(json, "advanced_supported"),
            CapabilitiesQueried = json.TryGetProperty("advanced_capabilities", out var capabilities) && JsonBool(capabilities, "queried"),
            DynamicBitrateSupported = capabilities.ValueKind == JsonValueKind.Object && JsonBool(capabilities, "dynamic_bitrate"),
            CustomVbvSupported = capabilities.ValueKind == JsonValueKind.Object && JsonBool(capabilities, "custom_vbv"),
            TemporalAqSupported = capabilities.ValueKind == JsonValueKind.Object && JsonBool(capabilities, "temporal_aq"),
            Requested = new()
            {
                QualityTarget = Has(AdvancedEncodingFlags.QualityTarget) ? requested.GetProperty("cq256").GetUInt32() / 256.0 : null,
                MaximumQp = Has(AdvancedEncodingFlags.MaximumQp) ? requested.GetProperty("max_qp").GetInt32() : null,
                PeakMbps = Has(AdvancedEncodingFlags.PeakBitrate) ? requested.GetProperty("peak_bitrate").GetUInt32() / 1_000_000.0 : null,
                VbvMbit = Has(AdvancedEncodingFlags.VbvBuffer) ? requested.GetProperty("vbv_bits").GetUInt32() / 1_000_000.0 : null,
                VbvDelayMbit = Has(AdvancedEncodingFlags.VbvDelay) ? requested.GetProperty("vbv_delay_bits").GetUInt32() / 1_000_000.0 : null,
                CbQpOffset = Has(AdvancedEncodingFlags.ChromaOffsets) ? requested.GetProperty("cb_qp_offset").GetInt32() : null,
                CrQpOffset = Has(AdvancedEncodingFlags.ChromaOffsets) ? requested.GetProperty("cr_qp_offset").GetInt32() : null,
                SpatialAq = Has(AdvancedEncodingFlags.SpatialAq) ? JsonBool(requested, "spatial_aq") : null,
                AqStrength = requested.GetProperty("aq_strength").GetInt32(),
                TemporalAq = Has(AdvancedEncodingFlags.TemporalAq) ? JsonBool(requested, "temporal_aq") : null,
                Preset = Has(AdvancedEncodingFlags.Preset) ? requested.GetProperty("preset").GetInt32() : null,
                StrictGopTarget = Has(AdvancedEncodingFlags.StrictGopTarget) ? JsonBool(requested, "strict_gop_target") : null,
                LowDelayKeyFrameScale = Has(AdvancedEncodingFlags.KeyFrameScale) ? requested.GetProperty("keyframe_scale").GetInt32() : null
            },
            AppliedFlags = (AdvancedEncodingFlags)json.GetProperty("applied_advanced_flags").GetUInt32(),
            PendingFlags = (AdvancedEncodingFlags)json.GetProperty("pending_advanced_flags").GetUInt32(),
            RejectedFlags = (AdvancedEncodingFlags)json.GetProperty("rejected_advanced_flags").GetUInt32(), LastError = json.GetProperty("last_advanced_error").GetUInt32(),
            QualityTarget = effective.GetProperty("cq256").GetUInt32() / 256.0, MaximumQpEnabled = JsonBool(effective, "max_qp_enabled"),
            MaximumQpI = effective.GetProperty("max_qp_i").GetUInt32(), MaximumQpP = effective.GetProperty("max_qp_p").GetUInt32(), MaximumQpB = effective.GetProperty("max_qp_b").GetUInt32(),
            SpatialAq = JsonBool(effective, "spatial_aq"), AqStrength = effective.GetProperty("aq_strength").GetUInt32(), TemporalAq = JsonBool(effective, "temporal_aq"),
            CbQpOffset = effective.GetProperty("cb_qp_offset").GetInt32(), CrQpOffset = effective.GetProperty("cr_qp_offset").GetInt32(),
            StrictGopTarget = effective.TryGetProperty("strict_gop_target", out _) ? JsonBool(effective, "strict_gop_target") : null,
            LowDelayKeyFrameScale = effective.TryGetProperty("keyframe_scale", out var scale) ? scale.GetUInt32() : null
        };
    }
    private static EncoderCallbackTiming? ReadCallbackTiming(JsonElement json)
    {
        if (!json.TryGetProperty("timing", out var timing)) return null;
        return new()
        {
            ReconfigureCalls = timing.GetProperty("reconfigure_calls").GetUInt64(), ReconfigureTotalUs = timing.GetProperty("reconfigure_total_us").GetUInt64(),
            ReconfigureMaximumUs = timing.GetProperty("reconfigure_max_us").GetUInt64(), ReconfigureOver2Ms = timing.GetProperty("reconfigure_over_2ms").GetUInt64(),
            EncodeCalls = timing.GetProperty("encode_calls").GetUInt64(), EncodeTotalUs = timing.GetProperty("encode_total_us").GetUInt64(),
            EncodeMaximumUs = timing.GetProperty("encode_max_us").GetUInt64(), EncodeOver2Ms = timing.GetProperty("encode_over_2ms").GetUInt64()
            , CompletionCalls = timing.TryGetProperty("completion_calls", out var completion) ? completion.GetUInt64() : 0,
            CompletionTotalUs = timing.TryGetProperty("completion_total_us", out var completionTotal) ? completionTotal.GetUInt64() : 0,
            CompletionMaximumUs = timing.TryGetProperty("completion_max_us", out var completionMax) ? completionMax.GetUInt64() : 0,
            CompletionOver2Ms = timing.TryGetProperty("completion_over_2ms", out var completionOver) ? completionOver.GetUInt64() : 0
        };
    }

    private static QualityMapSnapshot? ReadQualityMap(JsonElement json)
    {
        if (!json.TryGetProperty("last_map_timestamp_seconds", out var timestamp)) return null;
        return new()
        {
            TimestampSeconds = timestamp.GetDouble(), Enabled = json.GetProperty("last_map_enabled").GetBoolean(),
            CenterOffset = json.GetProperty("last_map_center_offset").GetInt32(), PeripheralOffset = json.GetProperty("last_map_peripheral_offset").GetInt32(),
            Size = json.GetProperty("last_map_size").GetUInt32(), PeripheralTiles = json.GetProperty("last_map_peripheral_tiles").GetUInt32(),
            CenterTiles = json.TryGetProperty("last_map_center_tiles", out var centerTiles) ? centerTiles.GetUInt32() : 0,
            CenterChangedTiles = json.TryGetProperty("last_map_center_changed_tiles", out var centerChanged) ? centerChanged.GetUInt32() : 0,
            CenterClampedTiles = json.TryGetProperty("last_map_center_clamped_tiles", out var centerClamped) ? centerClamped.GetUInt32() : 0,
            CenterMinDelta = json.TryGetProperty("last_map_center_min_delta", out var centerMin) ? centerMin.GetInt32() : 0,
            CenterMaxDelta = json.TryGetProperty("last_map_center_max_delta", out var centerMax) ? centerMax.GetInt32() : 0,
            PeripheralChangedTiles = json.GetProperty("last_map_peripheral_changed_tiles").GetUInt32(),
            PeripheralSaturatedTiles = json.GetProperty("last_map_peripheral_saturated_tiles").GetUInt32(),
            PeripheralClampedTiles = json.GetProperty("last_map_peripheral_clamped_tiles").GetUInt32(),
            PeripheralMinDelta = json.GetProperty("last_map_peripheral_min_delta").GetInt32(), PeripheralMaxDelta = json.GetProperty("last_map_peripheral_max_delta").GetInt32(),
            ProtectedTiles = json.GetProperty("last_map_protected_tiles").GetUInt32(), ProtectedMismatches = json.GetProperty("last_map_protected_mismatches").GetUInt32(),
            NvencStatus = json.GetProperty("last_map_nvenc_status").GetUInt32()
        };
    }

    private static string NativeError(uint error) => error switch
    {
        1001 => "The loaded driver has an incompatible encoder layout. Restart SteamVR if the driver was updated while it was running.",
        1002 => "NVIDIA's installed NVENC API is unavailable. Install or repair the NVIDIA display driver; other encoders provide transport statistics only.",
        1003 => "This NVENC API version is unsupported; encoder monitoring was not installed.",
        1004 or 1005 or 1007 => "Encoder hook installation failed. Restart SteamVR before retrying.",
        1008 => "Quality settings were rejected or monitoring is stopped.",
        1009 => "The native helper could not write its status snapshot.",
        1010 => "Stopping encoder hooks failed; restart SteamVR to restore all original functions.",
        1011 => "Restart SteamVR to remove the older SteamFrameQuality helper before using this encoder probe.",
        1012 => "Video monitoring is available, but this driver's foveation layout is incompatible. Valve's quality map was preserved.",
        1013 => "NVENC policy was rejected or monitoring is stopped.",
        1014 => "Valve's encoder policy has not been restored yet. Reconnect the stream, then select Stop again; restoration hooks remain active.",
        1015 => "Advanced encoder settings were rejected. Check the ranges and buffer/delay relationship; the updated monitor must be running.",
        1016 => "A paired codec trial is loaded. Ordinary encoder hooks cannot run alongside it.",
        _ => $"Native encoder probe returned error {error}."
    };

    // Read the PE export table without loading native code into the settings application.
    private static Dictionary<string, uint> ReadExports(string path, string[]? required = null)
    {
        var bytes = File.ReadAllBytes(path);
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        if (bytes.Length < 256 || U16(0) != 0x5a4d) throw new InvalidOperationException("The native helper is not a Windows image.");
        var pe = checked((int)U32(0x3c));
        if (pe < 0 || pe > bytes.Length - 256 || U32(pe) != 0x4550 || U16(pe + 4) != 0x8664 || U16(pe + 24) != 0x20b || (U16(pe + 22) & 0x2000) == 0)
            throw new InvalidOperationException("The native helper is not a supported x64 Windows image.");
        var sections = U16(pe + 6); var sectionStart = pe + 24 + U16(pe + 20);
        int Offset(uint rva)
        {
            for (var i = 0; i < sections; ++i)
            {
                var section = checked(sectionStart + i * 40);
                var address = U32(section + 12); var rawSize = U32(section + 16);
                if (rva >= address && (ulong)rva < (ulong)address + rawSize)
                    return checked((int)(rva - address + U32(section + 20)));
            }
            throw new InvalidOperationException("The native helper has an invalid export address.");
        }
        var directoryRva = U32(pe + 24 + 112); var directorySize = U32(pe + 24 + 116); var table = Offset(directoryRva);
        var functionCount = U32(table + 20); var nameCount = U32(table + 24);
        if (functionCount > 4096 || nameCount > 4096) throw new InvalidOperationException("The helper export table is invalid.");
        var functions = Offset(U32(table + 28)); var names = Offset(U32(table + 32)); var ordinals = Offset(U32(table + 36));
        var found = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (var i = 0; i < nameCount; ++i)
        {
            var nameOffset = Offset(U32(checked(names + i * 4)));
            var end = Array.IndexOf(bytes, (byte)0, nameOffset);
            if (end < nameOffset || end - nameOffset > 256) throw new InvalidOperationException("The helper export name is invalid.");
            var name = Encoding.ASCII.GetString(bytes, nameOffset, end - nameOffset);
            var ordinal = U16(checked(ordinals + i * 2));
            if (ordinal >= functionCount) throw new InvalidOperationException("The helper export ordinal is invalid.");
            var rva = U32(checked(functions + ordinal * 4));
            if ((ulong)rva >= directoryRva && (ulong)rva < (ulong)directoryRva + directorySize)
                throw new InvalidOperationException("Forwarded exports are not supported in the encoder helper.");
            found[name] = rva;
        }
        foreach (var name in required ?? ["GetProbeVersion", "InspectDriver", "StartProbe", "SetQuality", "SetEncodingPolicy", "WriteStatus", "StopProbe"])
            if (!found.ContainsKey(name)) throw new InvalidOperationException($"The native helper is missing the {name} export.");
        return found;
    }

    private static nint ResolveRemoteSystemExport(List<Module> modules, string library, string export)
    {
        var localLibrary = GetModuleHandleW(library);
        var address = GetProcAddress(localLibrary, export);
        nint owner = 0;
        if (address == 0 || !GetModuleHandleExW(0x00000004 | 0x00000002, address, out owner)) ThrowWin32();
        var path = new StringBuilder(32768);
        if (GetModuleFileNameW(owner, path, path.Capacity) == 0) ThrowWin32();
        var name = Path.GetFileName(path.ToString());
        var remote = modules.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The remote Windows loader module is unavailable.");
        var offset = address - owner;
        if (offset < 0 || offset >= remote.Size) throw new InvalidOperationException("The remote Windows loader address is invalid.");
        return remote.Base + offset;
    }

    private static SafeProcessHandle OpenTarget(int processId)
    {
        var handle = OpenProcess(0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400, false, processId);
        if (handle.IsInvalid) ThrowWin32();
        if (!IsWow64Process2(handle, out var machine, out var nativeMachine))
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
        }
        if (machine != 0 || nativeMachine != 0x8664) { handle.Dispose(); throw new InvalidOperationException("Encoder monitoring requires an x64 SteamVR server."); }
        return handle;
    }

    private static nint LoadRemoteHelper(SafeProcessHandle handle, List<Module> modules, string path)
    {
        // Preserve the complete x64 module result and GetLastError in the SAME
        // loader thread. A separate remote GetLastError call reads another TEB.
        byte[] stub = [0x53, 0x48, 0x83, 0xec, 0x20, 0x48, 0x8b, 0xd9,
            0x48, 0x8b, 0x4b, 0x10, 0x33, 0xd2, 0x41, 0xb8, 0x00, 0x11, 0x00, 0x00,
            0xff, 0x13, 0x48, 0x89, 0x43, 0x18,
            0xff, 0x53, 0x08, 0x89, 0x43, 0x20, 0x48, 0x83, 0xc4, 0x20, 0x5b, 0xc3];
        var pathBytes = Encoding.Unicode.GetBytes(path + "\0");
        var data = new byte[40 + pathBytes.Length];
        var argument = VirtualAllocEx(handle, 0, (nuint)data.Length, 0x1000 | 0x2000, 0x04);
        if (argument == 0) ThrowWin32();
        nint code = 0;
        var mayFree = true;
        try
        {
            // Resolve dependencies from this DLL's directory and Windows' standard
            // search locations, independent of SteamVR's working/search directory.
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(0), ResolveRemoteSystemExport(modules, "kernel32.dll", "LoadLibraryExW").ToInt64());
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), ResolveRemoteSystemExport(modules, "kernel32.dll", "GetLastError").ToInt64());
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), (argument + 40).ToInt64());
            pathBytes.CopyTo(data, 40);
            if (!WriteProcessMemory(handle, argument, data, (nuint)data.Length, out var dataWritten) || dataWritten != (nuint)data.Length) ThrowWin32();
            var readback = new byte[data.Length];
            if (!ReadProcessMemory(handle, argument, readback, (nuint)readback.Length, out var argumentRead) || argumentRead != (nuint)readback.Length) ThrowWin32();
            if (!data.AsSpan().SequenceEqual(readback)) throw new InvalidOperationException("The remote loader argument could not be verified. Monitoring was not installed.");
            code = VirtualAllocEx(handle, 0, (nuint)stub.Length, 0x1000 | 0x2000, 0x04);
            if (code == 0) ThrowWin32();
            if (!WriteProcessMemory(handle, code, stub, (nuint)stub.Length, out var codeWritten) || codeWritten != (nuint)stub.Length) ThrowWin32();
            if (!VirtualProtectEx(handle, code, (nuint)stub.Length, 0x20, out _) || !FlushInstructionCache(handle, code, (nuint)stub.Length)) ThrowWin32();
            _ = Invoke(handle, code, argument, () => mayFree = false);
            var result = new byte[40];
            if (!ReadProcessMemory(handle, argument, result, (nuint)result.Length, out var read) || read != (nuint)result.Length) ThrowWin32();
            if (BinaryPrimitives.ReadInt64LittleEndian(result.AsSpan(24)) == 0)
            {
                var error = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(32));
                throw new InvalidOperationException($"Windows could not load the encoder helper: {new Win32Exception(error).Message} (error {error}). Monitoring was not installed.");
            }
            return (nint)BinaryPrimitives.ReadInt64LittleEndian(result.AsSpan(24));
        }
        finally
        {
            // A timed-out loader still owns its code and parameters until SteamVR exits.
            if (mayFree)
            {
                if (code != 0) VirtualFreeEx(handle, code, 0, 0x8000);
                VirtualFreeEx(handle, argument, 0, 0x8000);
            }
        }
    }
    private static ulong ProcessCreationTime(Process process)
    {
        if (!GetProcessTimes(process.SafeHandle, out var creation, out _, out _, out _)) ThrowWin32();
        return creation;
    }
    private static void ValidateIdentity(SafeProcessHandle handle, ulong expected)
    {
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _)) ThrowWin32();
        if (creation != expected) throw new InvalidOperationException("SteamVR restarted. Select Start to monitor the new process.");
    }

    private static uint Invoke(SafeProcessHandle handle, nint address, byte[] argument)
    {
        var allocation = VirtualAllocEx(handle, 0, (nuint)argument.Length, 0x1000 | 0x2000, 0x04);
        if (allocation == 0) ThrowWin32();
        var mayFree = true;
        try
        {
            if (!WriteProcessMemory(handle, allocation, argument, (nuint)argument.Length, out var written) || written != (nuint)argument.Length) ThrowWin32();
            return Invoke(handle, address, allocation, () => mayFree = false);
        }
        finally { if (mayFree) VirtualFreeEx(handle, allocation, 0, 0x8000); }
    }
    private static uint Invoke(SafeProcessHandle handle, nint address, nint argument, Action? timedOut = null)
    {
        using var thread = CreateRemoteThread(handle, 0, 0, address, argument, 0, out _);
        if (thread.IsInvalid) ThrowWin32();
        var waited = WaitForSingleObject(thread, 10000);
        if (waited != 0)
        {
            // A timed-out thread can still be reading its argument. Never free it or unload its code.
            timedOut?.Invoke();
            throw new TimeoutException("The encoder helper did not respond. Restart SteamVR before retrying.");
        }
        if (!GetExitCodeThread(thread, out var result)) ThrowWin32();
        return result;
    }
    private static void ThrowWin32() => throw new Win32Exception(Marshal.GetLastWin32Error());
    private sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeThreadHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process2(SafeProcessHandle process, out ushort machine, out ushort nativeMachine);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint allocation, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] data, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] data, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint protection, out uint previous);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(SafeProcessHandle process, nint address, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeThreadHandle CreateRemoteThread(SafeProcessHandle process, nint attributes, nuint stack, nint start, nint argument, uint flags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeThreadHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeThread(SafeThreadHandle thread, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern nint GetProcAddress(nint module, string export);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetModuleFileNameW(nint module, StringBuilder path, int capacity);
}
