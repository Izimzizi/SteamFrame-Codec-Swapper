using System.Buffers.Binary;

namespace SteamFrame.Core;

/// <summary>Versioned runtime identity; it does not by itself prove encoded output.</summary>
public sealed record PairedCodecIdentity(string Codec, uint Fps, uint AverageBitrate, uint PeakBitrate, bool Cbr = false, uint Preset = 2, uint VbvFrames = 1)
{
    public static PairedCodecIdentity Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 36 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 36 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 1)
            throw new InvalidDataException("The paired codec identity interface is incompatible.");
        var codec = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) switch
        {
            1 => "AV1", 2 => "H.264", 3 => "HEVC", _ => throw new InvalidDataException("The paired codec identity is unknown.")
        };
        var fps = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var average = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        var peak = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        if (fps is not (72 or 80 or 90 or 120) || average is < 1_000_000 or > 500_000_000 || peak < average || peak > 500_000_000)
            throw new InvalidDataException("The paired codec identity settings are invalid.");
        var rateControl = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
        var preset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
        var vbv = BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]);
        if (rateControl > 1 || preset is < 1 or > 7 || vbv is < 1 or > 16) throw new InvalidDataException("The paired codec identity settings are invalid.");
        return new(codec, fps, average, peak, rateControl == 1, preset, vbv);
    }
}
