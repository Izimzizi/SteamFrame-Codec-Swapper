using System.Buffers.Binary;
using System.Diagnostics;

namespace SteamLinkVRSettings.Frame;

public sealed partial class EncoderProbe
{
    // Read the already loaded helper's versioned metadata publication. This never
    // executes a status thread in vrserver or writes a status file while measuring.
    internal Task<EncoderSnapshot> GetPassiveSnapshotAsync() => RunAsync(() =>
    {
        using var process = DiscoverProcess() ?? throw new IOException("SteamVR stopped during measurement.");
        var session = _session ?? throw new IOException("Start encoder monitoring before passive measurement.");
        if (process.Id != session.ProcessId || !_protocolVerified || !session.Exports.ContainsKey("ReadOnlyTelemetry"))
            throw new IOException("Restart SteamVR with the current encoder helper before benchmarking.");
        using var handle = OpenProcess(0x410, false, process.Id); // query + read only
        if (handle.IsInvalid) ThrowWin32();
        ValidateIdentity(handle, session.CreationTime);
        var module = FindHelper(ReadModules(process), session);
        var address = Address(module, session.Exports, "ReadOnlyTelemetry");
        if (session.Exports["ReadOnlyTelemetry"] > module.Size - 144) throw new IOException("Invalid telemetry bounds.");
        byte[] Read(int size)
        {
            var bytes = new byte[size];
            if (!ReadProcessMemory(handle, address, bytes, (nuint)size, out var count) || count != (nuint)size) ThrowWin32();
            return bytes;
        }
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var start = Stopwatch.GetTimestamp();
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(Read(8));
            if (sequence == 0 || (sequence & 1) != 0) { Thread.Sleep(1); continue; }
            var bytes = Read(144);
            var after = BinaryPrimitives.ReadUInt64LittleEndian(Read(8));
            if (sequence != after || sequence != BinaryPrimitives.ReadUInt64LittleEndian(bytes)) { Thread.Sleep(1); continue; }
            var now = (start / (double)Stopwatch.Frequency + Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency) / 2;
            return DecodeTelemetry(bytes, _lastSnapshot, session.ProcessId, session.CreationTime, now);
        }
        throw new IOException("Encoder telemetry was busy; no stale sample was used.");
    });

    internal static EncoderSnapshot DecodeTelemetry(byte[] data, EncoderSnapshot baseline, int pid, ulong creation, double now)
    {
        if (data.Length != 144) throw new IOException("Invalid encoder telemetry size.");
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
        var last = BitConverter.Int64BitsToDouble((long)U64(80));
        var flags = U32(120); var mode = U32(108);
        if (U32(8) != 1 || U32(12) != 144 || U64(0) == 0 || (U64(0) & 1) != 0 ||
            !double.IsFinite(now) || !double.IsFinite(last) || last < 0 || last > now + .005 ||
            U64(16) == 0 || (flags & ~511u) != 0 || (mode & ~0x1ffffu) != 0 ||
            U32(88) is < 32 or > 16384 || U32(92) is < 32 or > 16384 || baseline.EncodingPolicy is null)
            throw new IOException("Unsupported encoder telemetry publication.");
        var codec = (flags & 128) != 0 ? "HEVC" : (flags & 256) != 0 ? "H.264" : "Unknown";
        return baseline with
        {
            ProcessId=pid, Generation=$"{pid}-{creation}-{U64(16)}", PassiveConfigurationRevision=U64(24),
            TimestampSeconds=now, LastEncodedTimestampSeconds=last,
            Installed=(flags & 1)!=0, Active=(flags & 1)!=0 && last>0 && now-last<2,
            QualitySupported=(flags & 2)!=0, Enabled=(mode & 0x10000)!=0,
            CenterOffset=unchecked((sbyte)mode), PeripheralOffset=unchecked((sbyte)(mode>>8)),
            EncodedFrames=U64(32), EncodedBytes=U64(40), EncodeErrors=U64(48), LockErrors=U64(56),
            RejectedApiCalls=U64(64), Codec=codec, CodecObserved=codec!="Unknown" && U64(32)>0, BitDepth=U32(96),
            EncodingPolicy=baseline.EncodingPolicy with
            {
                Requested=Frame.EncodingPolicy.FromPacked(U32(112)), Applied=Frame.EncodingPolicy.FromPacked(U32(116)),
                Width=U32(88), Height=U32(92), RateControl=U32(100), MultiPass=U32(104),
                Observed=(flags & 4)!=0, Filler=(flags & 8)!=0, NeedsNewSession=(flags & 16)!=0,
                Unsupported=(flags & 32)!=0, InitializationRejected=(flags & 64)!=0, Errors=U64(72), Calls=U64(136)
            },
            AdvancedEncoding=baseline.AdvancedEncoding is { } advanced ? advanced with
            { PendingFlags=(AdvancedEncodingFlags)U32(124), RejectedFlags=(AdvancedEncodingFlags)U32(128), AppliedFlags=(AdvancedEncodingFlags)U32(132) } : null
        };
    }
}
