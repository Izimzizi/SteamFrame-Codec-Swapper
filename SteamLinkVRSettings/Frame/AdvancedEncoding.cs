using System.Buffers.Binary;

namespace SteamLinkVRSettings.Frame;

[Flags]
public enum AdvancedEncodingFlags : uint
{
    None = 0, QualityTarget = 1, MaximumQp = 2, PeakBitrate = 4, VbvBuffer = 8,
    VbvDelay = 16, ChromaOffsets = 32, SpatialAq = 64, TemporalAq = 128, Preset = 256,
    StrictGopTarget = 512, KeyFrameScale = 1024
}

/// <summary>Null means preserve Valve's value. A value of zero is an explicit value, never 'missing'.</summary>
public sealed record AdvancedEncoding
{
    public double? QualityTarget { get; init; }
    public int? MaximumQp { get; init; }
    public double? PeakMbps { get; init; }
    public double? VbvMbit { get; init; }
    public double? VbvDelayMbit { get; init; }
    public int? CbQpOffset { get; init; }
    public int? CrQpOffset { get; init; }
    public bool? SpatialAq { get; init; }
    public int AqStrength { get; init; } = 8;
    public bool? TemporalAq { get; init; }
    /// <summary>Null/zero preserves Valve; 1–7 selects P1–P7 after a supported reconnect.</summary>
    public int? Preset { get; init; }
    public bool? StrictGopTarget { get; init; }
    /// <summary>I/P bit ratio for CBR with a single-frame VBV. Null preserves Valve.</summary>
    public int? LowDelayKeyFrameScale { get; init; }
    public AdvancedEncodingFlags Flags => (QualityTarget.HasValue ? AdvancedEncodingFlags.QualityTarget : 0) |
        (MaximumQp.HasValue ? AdvancedEncodingFlags.MaximumQp : 0) | (PeakMbps.HasValue ? AdvancedEncodingFlags.PeakBitrate : 0) |
        (VbvMbit.HasValue ? AdvancedEncodingFlags.VbvBuffer : 0) | (VbvDelayMbit.HasValue ? AdvancedEncodingFlags.VbvDelay : 0) |
        (CbQpOffset.HasValue || CrQpOffset.HasValue ? AdvancedEncodingFlags.ChromaOffsets : 0) |
        (SpatialAq.HasValue ? AdvancedEncodingFlags.SpatialAq : 0) | (TemporalAq.HasValue ? AdvancedEncodingFlags.TemporalAq : 0) |
        (Preset is > 0 ? AdvancedEncodingFlags.Preset : 0) |
        (StrictGopTarget.HasValue ? AdvancedEncodingFlags.StrictGopTarget : 0) |
        (LowDelayKeyFrameScale.HasValue ? AdvancedEncodingFlags.KeyFrameScale : 0);
    public bool IsDefault => Flags == 0;
    public void Validate()
    {
        Range(QualityTarget, 0, 51, "VBR quality target"); Range(MaximumQp, 0, 51, "Maximum QP");
        Range(PeakMbps, 25, 500, "Peak bitrate"); Range(VbvMbit, .001, 64, "VBV buffer"); Range(VbvDelayMbit, 0, 64, "VBV initial delay");
        Range(CbQpOffset, -12, 12, "Cb offset"); Range(CrQpOffset, -12, 12, "Cr offset"); Range(AqStrength, 0, 15, "AQ strength"); Range(Preset, 0, 7, "Preset");
        Range(LowDelayKeyFrameScale, 1, 4, "Keyframe bit ratio");
        if (CbQpOffset.HasValue != CrQpOffset.HasValue) throw new ArgumentException("Override both chroma offsets together.");
        if (VbvMbit is double buffer && VbvDelayMbit is double delay && delay > buffer)
            throw new ArgumentException("VBV initial delay cannot exceed the overridden VBV buffer.");
    }
    public AdvancedEncoding Canonicalize() => this with
    {
        QualityTarget = QualityTarget.HasValue ? Math.Round(QualityTarget.Value * 256) / 256 : null,
        PeakMbps = PeakMbps.HasValue ? Math.Round(PeakMbps.Value * 1_000_000) / 1_000_000 : null,
        VbvMbit = VbvMbit.HasValue ? Math.Round(VbvMbit.Value * 1_000_000) / 1_000_000 : null,
        VbvDelayMbit = VbvDelayMbit.HasValue ? Math.Round(VbvDelayMbit.Value * 1_000_000) / 1_000_000 : null,
        Preset = Preset is > 0 ? Preset : null
    };
    private static void Range(double? value, double minimum, double maximum, string label)
    {
        if (value is double number && (!double.IsFinite(number) || number < minimum || number > maximum))
            throw new ArgumentOutOfRangeException(label, $"{label} must be between {minimum} and {maximum}.");
    }
    /// <summary>Fixed little-endian sixteen-DWORD contract. Version 2 uses the former reserved words.</summary>
    public byte[] ToNativeBytes()
    {
        Validate();
        var bytes = new byte[64];
        void Write(int index, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4, 4), value);
        Write(0, 64); Write(1, 2); Write(2, (uint)Flags);
        Write(3, (uint)Math.Round((QualityTarget ?? 0) * 256)); Write(4, (uint)(MaximumQp ?? 0));
        Write(5, (uint)Math.Round((PeakMbps ?? 0) * 1_000_000)); Write(6, (uint)Math.Round((VbvMbit ?? 0) * 1_000_000));
        Write(7, (uint)Math.Round((VbvDelayMbit ?? 0) * 1_000_000)); Write(8, unchecked((uint)(CbQpOffset ?? 0)));
        Write(9, unchecked((uint)(CrQpOffset ?? 0))); Write(10, SpatialAq == true ? 1u : 0); Write(11, (uint)AqStrength);
        Write(12, TemporalAq == true ? 1u : 0); Write(13, (uint)(Preset ?? 0));
        Write(14, StrictGopTarget == true ? 1u : 0); Write(15, (uint)(LowDelayKeyFrameScale ?? 0));
        return bytes;
    }
}

public sealed record AdvancedEncodingSnapshot
{
    public bool Supported { get; init; }
    public bool CapabilitiesQueried { get; init; }
    public bool DynamicBitrateSupported { get; init; }
    public bool CustomVbvSupported { get; init; }
    public bool TemporalAqSupported { get; init; }
    public AdvancedEncoding Requested { get; init; } = new();
    public AdvancedEncodingFlags AppliedFlags { get; init; }
    public AdvancedEncodingFlags PendingFlags { get; init; }
    public AdvancedEncodingFlags RejectedFlags { get; init; }
    public uint LastError { get; init; }
    public double QualityTarget { get; init; }
    public bool MaximumQpEnabled { get; init; }
    public uint MaximumQpI { get; init; }
    public uint MaximumQpP { get; init; }
    public uint MaximumQpB { get; init; }
    public bool SpatialAq { get; init; }
    public uint AqStrength { get; init; }
    public bool TemporalAq { get; init; }
    public int CbQpOffset { get; init; }
    public int CrQpOffset { get; init; }
    public bool? StrictGopTarget { get; init; }
    public uint? LowDelayKeyFrameScale { get; init; }
}

public sealed record EncoderCallbackTiming
{
    public ulong ReconfigureCalls { get; init; }
    public ulong ReconfigureTotalUs { get; init; }
    public ulong ReconfigureMaximumUs { get; init; }
    public ulong ReconfigureOver2Ms { get; init; }
    public ulong EncodeCalls { get; init; }
    public ulong EncodeTotalUs { get; init; }
    public ulong EncodeMaximumUs { get; init; }
    public ulong EncodeOver2Ms { get; init; }
    public ulong CompletionCalls { get; init; }
    public ulong CompletionTotalUs { get; init; }
    public ulong CompletionMaximumUs { get; init; }
    public ulong CompletionOver2Ms { get; init; }
}
