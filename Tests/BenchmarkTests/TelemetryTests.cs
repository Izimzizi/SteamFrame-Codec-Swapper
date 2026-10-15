using System.Buffers.Binary;
using SteamLinkVRSettings.Frame;

public class TelemetryTests
{
    private static byte[] Publication()
    {
        var b=new byte[144];
        void U32(int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset),value);
        void U64(int offset,ulong value)=>BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(offset),value);
        U64(0,2);U32(8,1);U32(12,144);U64(16,5);U64(24,7);
        U64(32,900);U64(40,300_000_000);U64(80,(ulong)BitConverter.DoubleToInt64Bits(100));
        U32(88,1664);U32(92,1664);U32(96,10);U32(100,2);U32(104,1);
        U32(108,0x10000|236|244<<8);U32(112,10);U32(116,10);U32(120,1|2|4|128);
        return b;
    }
    private static EncoderSnapshot Baseline()=>new() {EncodingPolicy=new(),AdvancedEncoding=new()};
    [Fact] public void PublishedCountersPreserveIdentityAndAcceptedConfiguration()
    {
        var s=EncoderProbe.DecodeTelemetry(Publication(),Baseline(),50,123,100.1);
        Assert.True(s.Active);Assert.Equal("50-123-5",s.Generation);Assert.Equal(7UL,s.PassiveConfigurationRevision);
        Assert.Equal(900UL,s.EncodedFrames);Assert.Equal(300_000_000UL,s.EncodedBytes);Assert.Equal("HEVC",s.Codec);
        Assert.Equal(-20,s.CenterOffset);Assert.Equal(-12,s.PeripheralOffset);Assert.Equal(2U,s.EncodingPolicy!.RateControl);
        Assert.Equal(EncoderMultiPass.QuarterResolution,s.EncodingPolicy.Applied.MultiPass);Assert.False(s.EncodingPolicy.Pending);
    }
    [Theory][InlineData(0)][InlineData(8)][InlineData(12)][InlineData(16)][InlineData(88)]
    public void InvalidOrUnpublishedMetadataCannotBeAccepted(int offset)
    {
        var b=Publication();Array.Clear(b,offset,4);
        Assert.Throws<IOException>(()=>EncoderProbe.DecodeTelemetry(b,Baseline(),50,123,100.1));
    }
    [Fact] public void InProgressWriterAndStaleCountersAreNotFreshSamples()
    {
        var b=Publication();b[0]=3;
        Assert.Throws<IOException>(()=>EncoderProbe.DecodeTelemetry(b,Baseline(),50,123,100.1));
        Assert.False(EncoderProbe.DecodeTelemetry(Publication(),Baseline(),50,123,103).Active);
    }
}
