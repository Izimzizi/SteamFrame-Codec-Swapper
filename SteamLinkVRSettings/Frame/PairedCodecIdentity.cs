using System.Buffers.Binary;

namespace SteamLinkVRSettings.Frame;

/// <summary>Versioned runtime identity; it does not by itself prove encoded output.</summary>
public sealed record PairedCodecIdentity(string Codec, uint Fps, uint AverageBitrate, uint PeakBitrate)
{
    public static PairedCodecIdentity Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 24 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 24 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 1)
            throw new InvalidDataException("The paired codec identity interface is incompatible.");
        var codec = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) switch
        {
            1 => "AV1", 2 => "H.264", _ => throw new InvalidDataException("The paired codec identity is unknown.")
        };
        var fps = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var average = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        var peak = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        if (fps is not (90 or 120) || average is < 1_000_000 or > 500_000_000 || peak < average || peak > 500_000_000)
            throw new InvalidDataException("The paired codec identity settings are invalid.");
        return new(codec, fps, average, peak);
    }
}
