namespace SteamLinkVRSettings.Frame;

/// <summary>NVENC output metadata only. EncodedBytes excludes transport/FEC/duplicate links.</summary>
public sealed record EncoderSnapshot
{
    public int ProcessId { get; init; }
    public bool Supported { get; init; }
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
    public uint LastNvencStatus { get; init; }
    public string Codec { get; init; } = "Unknown";
    public string Message { get; init; } = "Encoder monitoring has not started.";
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
