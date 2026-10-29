using SteamFrame.Core;

namespace SteamFrame.Tests;

public class WirelessLinkStatusTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Realtek 8832CU Wireless LAN WiFi 6 USB NIC For Valve", "", true)]
    [InlineData("Killer(R) Wi-Fi 6E AX1675x 160MHz Wireless Network Adapter", "frame_5B2F4CDF", true)]
    [InlineData("Killer(R) Wi-Fi 6E AX1675x 160MHz Wireless Network Adapter", "HomeNetwork", false)]
    [InlineData("Valve Software Steam Frame Wireless Adapter", "", true)]
    [InlineData("Some Ethernet Controller", "", false)]
    public void The_dongle_is_recognized_by_adapter_description_or_headset_access_point(string description, string ssid, bool expected) =>
        Assert.Equal(expected, WirelessLinkStatus.IsValveDongle(description, ssid));

    [Theory]
    [InlineData(6135, 37, "802.11ax", "6 GHz")]
    [InlineData(5180, 36, "802.11ac", "5 GHz")]
    [InlineData(2447, 8, "802.11n", "2.4 GHz")]
    [InlineData(null, 37, "802.11ax", "6 GHz")]
    [InlineData(null, 36, "802.11ac", "5 GHz")]
    [InlineData(null, 8, "802.11n", "2.4 GHz")]
    [InlineData(null, 5, "802.11n", "2.4 GHz")]
    [InlineData(null, 5, "802.11ax", "2.4 or 6 GHz")]
    [InlineData(null, null, "802.11ax", "unknown band")]
    public void Band_is_derived_from_frequency_before_channel_number(int? frequency, int? channel, string phy, string expected) =>
        Assert.Equal(expected, WirelessLinkStatus.BandFor(frequency, channel, phy));

    [Fact]
    public void Snapshot_text_never_claims_video_throughput_and_flags_weak_radio()
    {
        var strong = new WirelessLinkSnapshot(true, true, "Realtek 8832CU Wireless LAN WiFi 6 USB NIC For Valve", "frame_5B2F4CDF", "6 GHz", 37, 6135, 160, "802.11ax", 2401.5, 2401.5, 90, -63, 12, 0, 1, 4000, DateTimeOffset.Now, "ok");
        Assert.Equal("Radio: 6 GHz channel 37 · 160 MHz · 802.11ax · PHY 2402 Mbps · -63 dBm", strong.Summary);
        Assert.Contains("not the video bitrate", strong.Detail);
        Assert.False(strong.Weak); Assert.False(strong.SlowPhy); Assert.False(strong.Narrow); Assert.False(strong.LegacyBand);
        Assert.Equal(0.003, strong.RetryShare!.Value, 6);
        var weak = strong with { Rssi = -74, ReceiveRateMbps = 600, BandwidthMhz = 40 };
        Assert.True(weak.Weak); Assert.True(weak.SlowPhy); Assert.True(weak.Narrow);
        var legacy = strong with { Band = "2.4 GHz" };
        Assert.True(legacy.LegacyBand);
        var unknown = WirelessLinkSnapshot.Unknown("No Valve wireless dongle is registered as a Wi-Fi interface.");
        Assert.StartsWith("Radio: No Valve", unknown.Summary);
        Assert.Null(unknown.RetryShare); Assert.False(unknown.Weak);
        var idle = strong with { Connected = false };
        Assert.Contains("not associated", idle.Summary);
        var noCounters = strong with { RetryDelta = null, TransmittedDelta = null };
        Assert.Contains("not reported", noCounters.Detail); Assert.Null(noCounters.RetryShare);
    }

    [Fact]
    public void A_live_read_never_throws_and_reports_unknowns_explicitly()
    {
        // Runs against whatever this PC has: a connected dongle, a bare one, or none at all.
        var first = WirelessLinkStatus.Read();
        var second = WirelessLinkStatus.Read();
        output.WriteLine(second.Summary);
        output.WriteLine(second.Detail);
        Assert.False(string.IsNullOrWhiteSpace(second.Summary));
        if (!second.Found) { Assert.Contains("Radio:", second.Summary); return; }
        if (!second.Connected) return;
        Assert.NotEqual("", second.PhyType);
        if (second.Rssi is int rssi) Assert.InRange(rssi, -120, 0);
        if (second.ReceiveRateMbps is double rx) Assert.InRange(rx, 1, 100_000);
        if (second.Channel is int channel) Assert.InRange(channel, 1, 233);
        if (second.SignalQuality is int quality) Assert.InRange(quality, 0, 100);
        if (second.RetryDelta is long retries) Assert.True(retries >= 0);
        Assert.True(first.Found);
    }
}
