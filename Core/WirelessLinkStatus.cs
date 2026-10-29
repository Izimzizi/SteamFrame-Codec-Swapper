using System.Runtime.InteropServices;
using System.Text;

namespace SteamFrame.Core;

/// <summary>
/// The Valve dongle's radio association as Windows reports it. Rates are PHY rates, not video throughput.
/// Retry counters are deltas between polls and only present when the driver advances them.
/// </summary>
public sealed record WirelessLinkSnapshot(
    bool Found, bool Connected, string Description, string Ssid, string Band, int? Channel, int? CenterFrequencyMhz, int? BandwidthMhz,
    string PhyType, double? ReceiveRateMbps, double? TransmitRateMbps, int? SignalQuality, int? Rssi,
    long? RetryDelta, long? FailedDelta, long? AckFailureDelta, long? TransmittedDelta, DateTimeOffset ObservedAt, string Message)
{
    public static WirelessLinkSnapshot Unknown(string message) => new(false, false, "", "", "", null, null, null, "", null, null, null, null, null, null, null, null, DateTimeOffset.Now, message);
    public string Summary => !Found ? "Radio: " + Message : !Connected ? "Radio: dongle not associated with the headset"
        : $"Radio: {Band}{(Channel is int c ? $" channel {c}" : "")}{(BandwidthMhz is int w ? $" · {w} MHz" : "")} · {PhyType}" +
          (ReceiveRateMbps is double rx ? $" · PHY {rx:0} Mbps" : "") + (Rssi is int rssi ? $" · {rssi} dBm" : SignalQuality is int q ? $" · {q}% signal" : "");
    public string Detail => !Found ? Message : !Connected ? "The dongle is present but has no wireless association. Keep the Frame awake with Wi-Fi enabled."
        : string.Join("\n", new[]
        {
            $"{Description}{(Ssid.Length > 0 ? $" → {Ssid}" : "")}",
            $"Signal {(Rssi is int r ? $"{r} dBm" : "unknown")}{(SignalQuality is int q ? $" ({q}%)" : "")} · PHY receive {(ReceiveRateMbps is double rx ? $"{rx:0} Mbps" : "unknown")} / transmit {(TransmitRateMbps is double tx ? $"{tx:0} Mbps" : "unknown")}",
            RetryDelta is long retries ? $"Frames retried since last poll: {retries}{(FailedDelta is long failed ? $", failed {failed}" : "")}{(AckFailureDelta is long acks ? $", ACK failures {acks}" : "")}{(TransmittedDelta is long sent ? $" of {sent} sent" : "")}" : "Retry counters are not reported by this driver.",
            "PHY rate and signal describe the radio, not the video bitrate the link delivers."
        });
    /// <summary>Retry share of transmitted frames since the previous poll, when the driver reports both.</summary>
    public double? RetryShare => RetryDelta is long retries && TransmittedDelta is long sent && sent > 0 ? Math.Clamp(retries / (double)sent, 0, 1) : null;
    public bool Weak => Connected && (Rssi is < -70 || SignalQuality is < 55);
    public bool SlowPhy => Connected && ReceiveRateMbps is < 800;
    public bool Narrow => Connected && BandwidthMhz is < 80;
    public bool LegacyBand => Connected && Band == "2.4 GHz";
}

/// <summary>Reads the Valve wireless dongle's association from the Windows WLAN service; no pairing, driver or headset access.</summary>
public static class WirelessLinkStatus
{
    private const int WlanIntfOpcodeCurrentConnection = 7, WlanIntfOpcodeChannelNumber = 8, WlanIntfOpcodeRealtimeConnectionQuality = 19;
    private const int WlanIntfOpcodeStatistics = 0x10000101, WlanIntfOpcodeRssi = 0x10000102;
    private const int InterfaceStateConnected = 1;

    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(nint handle, nint reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(nint handle, nint reserved, out nint interfaceList);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(nint handle, ref Guid interfaceGuid, int opcode, nint reserved, out uint dataSize, out nint data, nint opcodeValueType);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(nint memory);

    private static readonly object Gate = new();
    private static Guid? _lastInterface;
    private static (ulong Transmitted, ulong Retries, ulong Failed, ulong AckFailures)? _previousCounters;

    /// <summary>Matches the dongle by its adapter description or by the Frame's own access-point name.</summary>
    internal static bool IsValveDongle(string description, string ssid)
        => FrameTrafficMonitor.Classify(description) == 2 || ssid.StartsWith("frame_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Band from channel, phy type and (when Windows reports it) the center frequency.</summary>
    internal static string BandFor(int? centerFrequencyMhz, int? channel, string phyType)
    {
        if (centerFrequencyMhz is int mhz)
            return mhz >= 5925 ? "6 GHz" : mhz >= 4900 ? "5 GHz" : mhz >= 2400 ? "2.4 GHz" : "unknown band";
        if (channel is not int c) return "unknown band";
        if (c >= 36 && c % 4 == 0 && c <= 177) return "5 GHz";
        // Channels 1, 5, 9 and 13 exist on both 2.4 and 6 GHz. Without a center frequency only a
        // pre-802.11ax PHY settles it, because 6 GHz is 802.11ax or newer.
        if (c <= 14) return c % 4 == 1 && phyType is "802.11ax" or "802.11be" ? "2.4 or 6 GHz" : "2.4 GHz";
        if (c % 4 == 1 && c <= 233) return "6 GHz";
        return "unknown band";
    }

    internal static string PhyName(uint phy) => phy switch
    {
        1 => "FHSS", 2 => "DSSS", 3 => "IR", 4 => "802.11a", 5 => "802.11b", 6 => "802.11g", 7 => "802.11n", 8 => "802.11ac", 9 => "802.11ad", 10 => "802.11ax", 11 => "802.11be", _ => "unknown PHY"
    };

    public static WirelessLinkSnapshot Read()
    {
        lock (Gate)
        {
            nint handle = 0, list = 0;
            try
            {
                if (WlanOpenHandle(2, 0, out _, out handle) != 0) return WirelessLinkSnapshot.Unknown("Windows WLAN service unavailable.");
                if (WlanEnumInterfaces(handle, 0, out list) != 0 || list == 0) return WirelessLinkSnapshot.Unknown("Wireless interfaces could not be listed.");
                var count = (int)Marshal.ReadInt32(list);
                if (count < 0 || count > 64) return WirelessLinkSnapshot.Unknown("Wireless interface list was not recognized.");
                const int ItemSize = 16 + 512 + 4; // GUID, 256 wide characters, interface state.
                WirelessLinkSnapshot? presentButDisconnected = null;
                for (var i = 0; i < count; i++)
                {
                    var item = list + 8 + i * ItemSize;
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    var description = Marshal.PtrToStringUni(item + 16, 256)?.TrimEnd('\0') ?? "";
                    var state = Marshal.ReadInt32(item + 16 + 512);
                    var connection = state == InterfaceStateConnected ? QueryConnection(handle, guid) : null;
                    var ssid = connection?.Ssid ?? "";
                    if (!IsValveDongle(description, ssid)) continue;
                    if (connection is null)
                    {
                        presentButDisconnected ??= new(true, false, description, "", "", null, null, null, "", null, null, null, null, null, null, null, null, DateTimeOffset.Now, "Dongle present without a wireless association.");
                        continue;
                    }
                    var quality = QueryRealtimeQuality(handle, guid);
                    var channel = QueryInt(handle, guid, WlanIntfOpcodeChannelNumber);
                    var rssi = QueryInt(handle, guid, WlanIntfOpcodeRssi);
                    if (rssi is > 0 or < -120) rssi = quality?.Rssi;
                    rssi ??= quality?.Rssi;
                    var counters = QueryStatistics(handle, guid);
                    long? retryDelta = null, failedDelta = null, ackDelta = null, sentDelta = null;
                    if (counters is { } now)
                    {
                        if (_previousCounters is { } previous && _lastInterface == guid && now.Transmitted >= previous.Transmitted && now.Retries >= previous.Retries)
                        {
                            sentDelta = (long)Math.Min(now.Transmitted - previous.Transmitted, long.MaxValue);
                            retryDelta = (long)Math.Min(now.Retries - previous.Retries, long.MaxValue);
                            failedDelta = now.Failed >= previous.Failed ? (long)Math.Min(now.Failed - previous.Failed, long.MaxValue) : null;
                            ackDelta = now.AckFailures >= previous.AckFailures ? (long)Math.Min(now.AckFailures - previous.AckFailures, long.MaxValue) : null;
                            if (sentDelta == 0 && retryDelta == 0 && now.Transmitted == 0) { retryDelta = failedDelta = ackDelta = sentDelta = null; }
                        }
                        _previousCounters = now; _lastInterface = guid;
                    }
                    var phy = PhyName(connection.Value.PhyType);
                    var frequency = quality?.CenterFrequencyMhz;
                    return new(true, true, description, ssid, BandFor(frequency, channel, phy), channel, frequency, quality is { BandwidthMhz: > 0 } q ? q.BandwidthMhz : null, phy,
                        connection.Value.RxKbps > 0 ? connection.Value.RxKbps / 1000.0 : null, connection.Value.TxKbps > 0 ? connection.Value.TxKbps / 1000.0 : null,
                        connection.Value.SignalQuality is <= 100 ? (int)connection.Value.SignalQuality : null, rssi,
                        retryDelta, failedDelta, ackDelta, sentDelta, DateTimeOffset.Now, "Dongle association read from the Windows WLAN service.");
                }
                return presentButDisconnected ?? WirelessLinkSnapshot.Unknown("No Valve wireless dongle is registered as a Wi-Fi interface.");
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or AccessViolationException or ArgumentException)
            {
                return WirelessLinkSnapshot.Unknown("Wireless status is unavailable on this system.");
            }
            finally
            {
                if (list != 0) WlanFreeMemory(list);
                if (handle != 0) WlanCloseHandle(handle, 0);
            }
        }
    }

    private static (string Ssid, uint PhyType, uint SignalQuality, uint RxKbps, uint TxKbps)? QueryConnection(nint handle, Guid guid)
    {
        nint data = 0;
        try
        {
            if (WlanQueryInterface(handle, ref guid, WlanIntfOpcodeCurrentConnection, 0, out var size, out data, 0) != 0 || data == 0 || size < 4 + 4 + 512 + 36 + 4 + 4 + 4 + 4 + 4 + 4) return null;
            // WLAN_CONNECTION_ATTRIBUTES: state, mode, profile name[256], then WLAN_ASSOCIATION_ATTRIBUTES.
            var association = data + 4 + 4 + 512;
            var ssidLength = (int)Math.Clamp(Marshal.ReadInt32(association), 0, 32);
            var ssidBytes = new byte[ssidLength];
            Marshal.Copy(association + 4, ssidBytes, 0, ssidLength);
            var ssid = Encoding.UTF8.GetString(ssidBytes).TrimEnd('\0');
            // DOT11_SSID (36 bytes), DOT11_BSS_TYPE (4), BSSID (6 + 2 padding), PHY type (4), PHY index (4), signal quality (4), rx (4), tx (4).
            var phy = (uint)Marshal.ReadInt32(association + 36 + 4 + 8);
            var signal = (uint)Marshal.ReadInt32(association + 36 + 4 + 8 + 4 + 4);
            var rx = (uint)Marshal.ReadInt32(association + 36 + 4 + 8 + 4 + 4 + 4);
            var tx = (uint)Marshal.ReadInt32(association + 36 + 4 + 8 + 4 + 4 + 4 + 4);
            return (ssid, phy, signal, rx, tx);
        }
        finally { if (data != 0) WlanFreeMemory(data); }
    }

    private static int? QueryInt(nint handle, Guid guid, int opcode)
    {
        nint data = 0;
        try
        {
            if (WlanQueryInterface(handle, ref guid, opcode, 0, out var size, out data, 0) != 0 || data == 0 || size < 4) return null;
            return Marshal.ReadInt32(data);
        }
        catch (Exception error) when (error is AccessViolationException) { return null; }
        finally { if (data != 0) WlanFreeMemory(data); }
    }

    /// <summary>Windows 11 realtime connection quality: per-link center frequency, bandwidth and RSSI. Absent on older builds.</summary>
    private static (int CenterFrequencyMhz, int BandwidthMhz, int Rssi)? QueryRealtimeQuality(nint handle, Guid guid)
    {
        nint data = 0;
        try
        {
            if (WlanQueryInterface(handle, ref guid, WlanIntfOpcodeRealtimeConnectionQuality, 0, out var size, out data, 0) != 0 || data == 0) return null;
            // WLAN_REALTIME_CONNECTION_QUALITY: phy (4), link quality (4), rx (4), tx (4), MLO flag (4), link count (4), then links.
            if (size < 24 + 4 + 4 + 4 + 4) return null;
            var links = Marshal.ReadInt32(data + 20);
            if (links < 1) return null;
            // WLAN_REALTIME_CONNECTION_QUALITY_LINK_INFO: link id (1 + 3 padding), center frequency (4), bandwidth (4), rssi (4), rate set.
            var link = data + 24;
            var frequency = Marshal.ReadInt32(link + 4);
            var bandwidth = Marshal.ReadInt32(link + 8);
            var rssi = Marshal.ReadInt32(link + 12);
            if (frequency is < 2000 or > 8000 || bandwidth is < 0 or > 640 || rssi is > 0 or < -120) return null;
            // Some drivers report the frequency and RSSI but leave the channel width at zero.
            return (frequency, bandwidth, rssi);
        }
        catch (Exception error) when (error is AccessViolationException) { return null; }
        finally { if (data != 0) WlanFreeMemory(data); }
    }

    private static (ulong Transmitted, ulong Retries, ulong Failed, ulong AckFailures)? QueryStatistics(nint handle, Guid guid)
    {
        nint data = 0;
        try
        {
            if (WlanQueryInterface(handle, ref guid, WlanIntfOpcodeStatistics, 0, out var size, out data, 0) != 0 || data == 0) return null;
            // WLAN_STATISTICS: 3 × ULONGLONG, two WLAN_MAC_FRAME_STATISTICS (12 × ULONGLONG each), phy count (4 + 4 padding), then WLAN_PHY_FRAME_STATISTICS (18 × ULONGLONG).
            const int PhyCountOffset = 8 * 3 + 8 * 12 * 2;
            if (size < PhyCountOffset + 8 + 8 * 18) return null;
            var phys = Marshal.ReadInt32(data + PhyCountOffset);
            if (phys < 1) return null;
            var phy = data + PhyCountOffset + 8;
            var transmitted = (ulong)Marshal.ReadInt64(phy);
            var failed = (ulong)Marshal.ReadInt64(phy + 8 * 2);
            var retries = (ulong)Marshal.ReadInt64(phy + 8 * 3);
            var ackFailures = (ulong)Marshal.ReadInt64(phy + 8 * 9);
            return (transmitted, retries, failed, ackFailures);
        }
        catch (Exception error) when (error is AccessViolationException) { return null; }
        finally { if (data != 0) WlanFreeMemory(data); }
    }
}
