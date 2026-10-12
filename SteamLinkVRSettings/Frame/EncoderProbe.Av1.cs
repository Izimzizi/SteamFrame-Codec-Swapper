using System.Buffers.Binary;
using System.Diagnostics;

namespace SteamLinkVRSettings.Frame;

public sealed partial class EncoderProbe
{
    private int _av1ProcessId;
    private ulong _av1Creation;
    private readonly EncoderOutputActivity _av1Activity = new();
    private bool _av1Stopped;
    private EncoderSnapshot _lastAv1Snapshot = new();

    private void ResetAv1State()
    {
        _av1ProcessId = 0; _av1Creation = 0; _av1Stopped = false;
        _av1Activity.Reset(); _lastAv1Snapshot = new();
    }

    // Reads only an already-loaded, ABI-guarded lab adapter. Never installs or
    // changes its hooks, codec, bitrate, map, kernel or paired client.
    private EncoderSnapshot ReadAv1Snapshot(Process process, Module module)
    {
        var creation = ProcessCreationTime(process);
        if (_av1ProcessId != process.Id || _av1Creation != creation)
        {
            _av1ProcessId = process.Id; _av1Creation = creation;
            _av1Activity.Reset(); _av1Stopped = false; _sessionFaulted = false;
            _lastAv1Snapshot = new() { ProcessId = process.Id, PairedCodecTrialPresent = true };
        }
        _session = null; _protocolVerified = false; // Never reuse ordinary HEVC hooks for this adapter.
        if (_av1Stopped) return _lastAv1Snapshot with { Installed = false, Active = false,
            Message = "Paired codec monitoring stopped. Use the codec switcher to restore HEVC." };
        if (_sessionFaulted) throw new InvalidOperationException("The paired codec statistics call timed out. Restart SteamVR before another native call.");
        var exports = ReadExports(module.Path, ["Av1ReadTrialStats", "Av1CreateLegacyFunctionTable"]);
        if (!exports.ContainsKey("Av1ReadTrialStats") || !exports.ContainsKey("Av1CreateLegacyFunctionTable"))
            return new() { ProcessId = process.Id, PairedCodecTrialPresent = true, Message = "This paired codec adapter's statistics interface is unsupported." };
        using var handle = OpenTarget(process.Id);
        ValidateIdentity(handle, creation);
        const int size = 304; // Av1BridgeStats v1, audited x64 layout.
        var bytes = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        var allocation = VirtualAllocEx(handle, 0, size, 0x1000 | 0x2000, 0x04);
        if (allocation == 0) ThrowWin32();
        var mayFree = true;
        try
        {
            if (!WriteProcessMemory(handle, allocation, bytes, size, out var written) || written != size) ThrowWin32();
            var result = Invoke(handle, Address(module, exports, "Av1ReadTrialStats"), allocation, () => mayFree = false);
            if (result != 0) return new() { ProcessId = process.Id, PairedCodecTrialPresent = true, Message = "The paired codec statistics structure was rejected; restart with a compatible adapter." };
            if (!ReadProcessMemory(handle, allocation, bytes, size, out var read) || read != size) ThrowWin32();
        }
        finally { if (mayFree) VirtualFreeEx(handle, allocation, 0, 0x8000); }
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
        if (U32(0) != size || U32(4) != 1) throw new IOException("Discarded an incompatible AV1 statistics record.");
        PairedCodecIdentity? identity = null;
        if (exports.ContainsKey("Av1ReadTrialIdentity"))
        {
            var identityBytes = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(identityBytes.AsSpan(0), 24);
            BinaryPrimitives.WriteUInt32LittleEndian(identityBytes.AsSpan(4), 1);
            var identityAllocation = VirtualAllocEx(handle, 0, 24, 0x1000 | 0x2000, 0x04);
            if (identityAllocation == 0) ThrowWin32();
            var mayFreeIdentity = true;
            try
            {
                if (!WriteProcessMemory(handle, identityAllocation, identityBytes, 24, out var written) || written != 24) ThrowWin32();
                if (Invoke(handle, Address(module, exports, "Av1ReadTrialIdentity"), identityAllocation, () => mayFreeIdentity = false) != 0)
                    throw new InvalidDataException("The paired codec identity was rejected.");
                if (!ReadProcessMemory(handle, identityAllocation, identityBytes, 24, out var read) || read != 24) ThrowWin32();
                identity = PairedCodecIdentity.Read(identityBytes);
            }
            finally { if (mayFreeIdentity) VirtualFreeEx(handle, identityAllocation, 0, 0x8000); }
        }
        var now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        var frames = U64(40);
        var activity = _av1Activity.Sample($"paired-{process.Id}-{creation}-{identity?.Codec ?? "unknown"}", frames, now);
        var observed = identity is not null && U64(24) > 0 && frames > 0;
        return _lastAv1Snapshot = new()
        {
            ProcessId = process.Id, Supported = true, Installed = true,
            Active = activity.Active,
            EncodedFrames = frames, EncodedBytes = U64(48), Frames = U64(32), EncodeErrors = U64(56),
            TimestampSeconds = now, LastEncodedTimestampSeconds = activity.LastOutput,
            Generation = activity.Generation, LastNvencStatus = U32(88),
            Codec = observed ? identity!.Codec : "Unknown", CodecObserved = observed,
            CodecSource = "Paired codec identity, accepted initialization and encoded output",
            PairedCodecTrialPresent = true, BitDepth = observed ? 8u : null,
            EncodingPolicy = new()
            {
                Observed = U64(24) > 0, RateControl = 1, MultiPass = 0,
                AverageBitrate = U32(80), MaxBitrate = U32(84),
                VbvSize = identity is not null ? U32(80) / identity.Fps : 0,
                Width = U32(72), Height = U32(76), FrameRateNum = identity?.Fps ?? U32(100), FrameRateDen = identity is not null ? 1u : U32(104),
                Preset = "P2", Filler = false, Calls = U64(24) + U64(64), Errors = U64(56)
            },
            Message = identity is null ? "Paired codec identity is unavailable. Restart the codec session with the updated runtime; the active codec is unconfirmed."
                : activity.Active ? $"Measuring the paired {identity.Codec} trial. App foveation and policy overrides remain disabled."
                : "Paired codec adapter is loaded; waiting for fresh encoded output to confirm an active stream."
        };
    }
}
