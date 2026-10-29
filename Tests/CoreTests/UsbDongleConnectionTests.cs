using System.Buffers.Binary;
using SteamFrame.Core;

namespace SteamFrame.Tests;

public class UsbDongleConnectionTests
{
    [Theory]
    [InlineData(2, 0u, UsbDongleSpeed.HighSpeed)]
    [InlineData(2, 2u, UsbDongleSpeed.HighSpeed)] // USB 3 capable, currently operating at USB 2.
    [InlineData(2, 10u, UsbDongleSpeed.HighSpeed)] // Even SuperSpeedPlus capability is not operation.
    [InlineData(2, 3u, UsbDongleSpeed.SuperSpeed)] // Legacy EX often caps its Speed at HighSpeed.
    [InlineData(2, 15u, UsbDongleSpeed.SuperSpeedPlus)]
    [InlineData(0, 0u, UsbDongleSpeed.LowSpeed)]
    [InlineData(1, 0u, UsbDongleSpeed.FullSpeed)]
    [InlineData(255, 0u, UsbDongleSpeed.Unknown)]
    public void NegotiatedSpeedUsesOperatingFlags(byte legacySpeed, uint flags, UsbDongleSpeed expected) =>
        Assert.Equal(expected, UsbDongleConnection.DecodeSpeed(legacySpeed, flags));

    [Fact]
    public void MissingV2DoesNotMislabelAnExHighSpeedResultAsUsb2()
    {
        Assert.Equal(UsbDongleSpeed.Unknown, UsbDongleConnection.DecodeSpeed(2, null));
        Assert.Equal(UsbDongleSpeed.SuperSpeed, UsbDongleConnection.DecodeSpeed(3, null));
    }

    [Fact]
    public void PackedDescriptorRequiresExactConnectedValveDongle()
    {
        var bytes = new byte[35];
        bytes[4] = 18; bytes[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 0x28de);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 0x2432);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(31), 1);
        Assert.True(UsbDongleConnection.IsValveDongle(bytes));
        Assert.False(UsbDongleConnection.IsValveDongle(bytes.AsSpan(0, 34)));
        bytes[31] = 0; Assert.False(UsbDongleConnection.IsValveDongle(bytes));
        bytes[31] = 2; Assert.False(UsbDongleConnection.IsValveDongle(bytes));
        bytes[31] = 1; bytes[14] = 0; Assert.False(UsbDongleConnection.IsValveDongle(bytes));
        bytes[14] = 0x32; bytes[12] = 0; Assert.False(UsbDongleConnection.IsValveDongle(bytes));
    }

    [Fact]
    public void MissingAndUninspectableAreDifferentAndNeverRetainUsb3()
    {
        Assert.Equal("Dongle USB: not connected", new UsbDongleSnapshot([], true, DateTimeOffset.Now).Summary);
        Assert.Equal("Dongle USB: unknown", UsbDongleSnapshot.Unknown.Summary);
        Assert.Contains("unavailable", UsbDongleSnapshot.Unknown.Detail);
        Assert.DoesNotContain("USB 3", UsbDongleSnapshot.Unknown.Detail);
    }

    [Fact]
    public void MixedDonglesExposeSlowConnectionInsteadOfPickingTheFastOne()
    {
        var mixed = new UsbDongleSnapshot([new(UsbDongleSpeed.SuperSpeed), new(UsbDongleSpeed.HighSpeed)], true, DateTimeOffset.Now);
        Assert.Equal("Dongle USB: USB 3 / USB 2", mixed.Summary);
        Assert.True(mixed.HasSlowConnection);
        Assert.Contains("Dongle 2: USB 2", mixed.Detail);
        Assert.Contains("limits high-bitrate streaming", mixed.Detail);
        Assert.Contains("not the Wi-Fi rate", mixed.Detail);
    }
}
