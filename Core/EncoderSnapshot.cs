namespace SteamFrame.Core;


/// <summary>NVENC output metadata only. EncodedBytes excludes transport/FEC/duplicate links.</summary>
public sealed record EncoderSnapshot
{
    public ulong PassiveConfigurationRevision { get; init; }
    public int ProcessId { get; init; }
    public bool Supported { get; init; }
    /// <summary>A loaded helper cannot be replaced safely until this SteamVR process exits.</summary>
    public bool RequiresSteamVrRestart { get; init; }
    /// <summary>SteamVR or its streaming driver has not finished starting.</summary>
    public bool WaitingForDriver { get; init; }
    public bool QualitySupported { get; init; }
    public bool Installed { get; init; }
    public bool Enabled { get; init; }
    public bool Active { get; init; }
    public int CenterOffset { get; init; }
    public int PeripheralOffset { get; init; }
    public ulong EncodedBytes { get; init; }
    public ulong EncodedFrames { get; init; }
    /// <summary>Windows QPC time in seconds; use deltas only within the same Generation.</summary>
    public double TimestampSeconds { get; init; }
    public double LastEncodedTimestampSeconds { get; init; }
    public string Generation { get; init; } = "";
    public ulong EncodeErrors { get; init; }
    public ulong LockErrors { get; init; }
    public ulong RejectedApiCalls { get; init; }
    public ulong Frames { get; init; }
    public ulong ValidMaps { get; init; }
    public ulong ModifiedFrames { get; init; }
    public QualityMapSnapshot? LastQualityMap { get; init; }
    /// <summary>Cumulative whole-frame QP metadata, not the center's absolute QP.</summary>
    public ulong QuantizerSamples { get; init; }
    public ulong QuantizerSum { get; init; }
    public uint? LastFrameQp { get; init; }
    public uint? LastFrameBytes { get; init; }
    public EncodingPolicySnapshot? EncodingPolicy { get; init; }
    public AdvancedEncodingSnapshot? AdvancedEncoding { get; init; }
    public EncoderCallbackTiming? CallbackTiming { get; init; }
    public uint LastNvencStatus { get; init; }
    public string Codec { get; init; } = "Unknown";
    /// <summary>Codec metadata was verified with encoded output, not a setting or loaded DLL name.</summary>
    public bool CodecObserved { get; init; }
    public string CodecSource { get; init; } = "";
    public bool CodecReadFailed { get; init; }
    /// <summary>The already-loaded paired codec trial owns the encoder; ordinary app hooks must stay disabled.</summary>
    public bool PairedCodecTrialPresent { get; init; }
    public uint? BitDepth { get; init; }
    public string Message { get; init; } = "Encoder monitoring has not started.";
}

public enum EncoderRateControl { ValveDefault, Vbr, Cbr }
public enum EncoderMultiPass { ValveDefault, SinglePass, QuarterResolution, FullResolution }

/// <summary>Only explicit public NVENC policy fields. Zero leaves Valve's configuration unchanged.</summary>
public sealed record EncodingPolicy(EncoderRateControl RateControl = EncoderRateControl.ValveDefault,
    EncoderMultiPass MultiPass = EncoderMultiPass.ValveDefault)
{
    public uint Packed => IsValid ? (uint)RateControl | ((uint)MultiPass << 2) : throw new ArgumentOutOfRangeException(nameof(RateControl));
    public bool IsValid => Enum.IsDefined(RateControl) && Enum.IsDefined(MultiPass);
    public bool IsDefault => Packed == 0;
    public string Description => $"{RateControl switch { EncoderRateControl.Vbr => "VBR", EncoderRateControl.Cbr => "CBR without padding", _ => "Valve rate control" }} / {MultiPass switch { EncoderMultiPass.SinglePass => "single pass", EncoderMultiPass.QuarterResolution => "quarter-resolution multipass", EncoderMultiPass.FullResolution => "full-resolution multipass", _ => "Valve multipass" }}";
    public static EncodingPolicy FromPacked(uint packed)
    {
        var policy = new EncodingPolicy((EncoderRateControl)(packed & 3), (EncoderMultiPass)((packed >> 2) & 3));
        if ((packed & ~15u) != 0 || !policy.IsValid) throw new ArgumentOutOfRangeException(nameof(packed));
        return policy;
    }
}

/// <summary>Last configuration accepted by a verified initialize/reconfigure API call, never inferred from the request.</summary>
public sealed record EncodingPolicySnapshot
{
    public EncodingPolicy Requested { get; init; } = new();
    public EncodingPolicy Applied { get; init; } = new();
    public bool Observed { get; init; }
    public uint RateControl { get; init; }
    public uint MultiPass { get; init; }
    public uint AverageBitrate { get; init; }
    public uint MaxBitrate { get; init; }
    public uint VbvSize { get; init; }
    public uint VbvDelay { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public uint FrameRateNum { get; init; }
    public uint FrameRateDen { get; init; }
    public bool Filler { get; init; }
    public string Preset { get; init; } = "Unknown";
    public ulong Calls { get; init; }
    public ulong Errors { get; init; }
    public ulong RejectedCalls { get; init; }
    public bool NeedsNewSession { get; init; }
    public bool Unsupported { get; init; }
    public bool InitializationRejected { get; init; }
    public bool? DynamicRateControlSupported { get; init; }
    public uint? SupportedRateControlModes { get; init; }
    public bool Pending => Observed && Requested != Applied;
    public string StatusMessage => !Observed ? "Waiting for an accepted encoder configuration."
        : InitializationRejected ? "NVENC rejected initialization with this policy; Valve's configuration remains active. Change the request before retrying."
        : Unsupported ? "This encoder does not advertise the requested rate-control mode. The accepted configuration remains active."
        : NeedsNewSession ? "The requested rate-control or multipass change needs a new encoder session. The accepted configuration remains active. Restart SteamVR with reapply enabled to activate it."
        : Pending ? $"Requested policy is not active. The encoder is using {RateControlName} / {MultiPassName}. Reconnect and verify the accepted configuration."
        : "Requested policy accepted.";
    public string RateControlName => RateControl switch { 0 => "constant QP", 1 => "VBR", 2 => "CBR", _ => $"mode {RateControl}" };
    public string MultiPassName => MultiPass switch { 0 => "single pass", 1 => "quarter-resolution multipass", 2 => "full-resolution multipass", _ => $"mode {MultiPass}" };
}

/// <summary>Metadata for the last validated map submitted to NVENC. Deltas are not absolute codec QPs.</summary>
public sealed record QualityMapSnapshot
{
    public double TimestampSeconds { get; init; }
    public bool Enabled { get; init; }
    public int CenterOffset { get; init; }
    public int PeripheralOffset { get; init; }
    public uint Size { get; init; }
    public uint CenterTiles { get; init; }
    public uint CenterChangedTiles { get; init; }
    public uint CenterClampedTiles { get; init; }
    public int CenterMinDelta { get; init; }
    public int CenterMaxDelta { get; init; }
    public uint PeripheralTiles { get; init; }
    public uint PeripheralChangedTiles { get; init; }
    public uint PeripheralSaturatedTiles { get; init; }
    public uint PeripheralClampedTiles { get; init; }
    public int PeripheralMinDelta { get; init; }
    public int PeripheralMaxDelta { get; init; }
    public uint ProtectedTiles { get; init; }
    public uint ProtectedMismatches { get; init; }
    public uint NvencStatus { get; init; }
}
