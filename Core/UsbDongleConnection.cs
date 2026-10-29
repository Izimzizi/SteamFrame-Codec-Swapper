using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SteamFrame.Core;

public enum UsbDongleSpeed { Unknown, LowSpeed, FullSpeed, HighSpeed, SuperSpeed, SuperSpeedPlus }

public sealed record UsbDongleLink(UsbDongleSpeed Speed)
{
    public string Name => Speed switch
    {
        UsbDongleSpeed.HighSpeed => "USB 2",
        UsbDongleSpeed.SuperSpeed or UsbDongleSpeed.SuperSpeedPlus => "USB 3",
        UsbDongleSpeed.LowSpeed or UsbDongleSpeed.FullSpeed => "USB 1 speed",
        _ => "Unknown"
    };
    public string Detail => Speed switch
    {
        UsbDongleSpeed.HighSpeed => "High-Speed · 480 Mbps signaling",
        UsbDongleSpeed.SuperSpeed => "SuperSpeed · 5 Gbps signaling",
        UsbDongleSpeed.SuperSpeedPlus => "SuperSpeedPlus",
        UsbDongleSpeed.FullSpeed => "Full-Speed · 12 Mbps signaling",
        UsbDongleSpeed.LowSpeed => "Low-Speed · 1.5 Mbps signaling",
        _ => "Windows could not read the negotiated speed"
    };
}

public sealed record UsbDongleSnapshot(IReadOnlyList<UsbDongleLink> Links, bool Complete, DateTimeOffset ObservedAt)
{
    public static UsbDongleSnapshot Unknown => new([], false, DateTimeOffset.Now);
    public bool HasSlowConnection => Links.Any(x => x.Speed is UsbDongleSpeed.LowSpeed or UsbDongleSpeed.FullSpeed or UsbDongleSpeed.HighSpeed);
    public string Summary => Links.Count switch
    {
        0 => Complete ? "Dongle USB: not connected" : "Dongle USB: unknown",
        1 => $"Dongle USB: {Links[0].Name}",
        _ => "Dongle USB: " + string.Join(" / ", Links.Select(x => x.Name))
    };
    public string Detail => (Links.Count == 0
        ? Complete ? "No Valve wireless dongle is connected to a USB hub." : "Windows could not inspect all USB hubs. The dongle's connection speed is unavailable."
        : string.Join("\n", Links.Select((x, index) => $"{(Links.Count > 1 ? $"Dongle {index + 1}: " : "")}{x.Name} — {x.Detail}.")))
        + (HasSlowConnection ? "\nUSB 2 limits high-bitrate streaming. Connect the dongle directly to a USB 3 port and check any hub or extension." : "")
        + (Links.Count > 0 ? "\nThis is the PC-to-dongle USB connection, not the Wi-Fi rate or measured video throughput." : "");
}

/// <summary>Reads the negotiated connection of Valve VID 28DE / PID 2432 from Windows USB hubs.</summary>
public static class UsbDongleConnection
{
    private static readonly Guid HubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
    private const uint GetNodeInformation = 0x220408;
    private const uint GetConnectionInformationEx = 0x220448;
    private const uint GetConnectionInformationExV2 = 0x22045c;
    private const int ConnectionInformationSize = 35; // usbioctl.h is packed to one byte.
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;

    // This may make kernel calls; callers run it on a worker and never await it on the graph's tick.
    public static UsbDongleSnapshot Read()
    {
        if (!OperatingSystem.IsWindows()) return UsbDongleSnapshot.Unknown;
        var links = new List<UsbDongleLink>();
        bool complete = true;
        try
        {
            var guid = HubInterface;
            var devices = SetupDiGetClassDevs(ref guid, null, 0, 0x12); // PRESENT | DEVICEINTERFACE
            if (devices == -1) return UsbDongleSnapshot.Unknown;
            try
            {
                for (uint index = 0; ; index++)
                {
                    var device = new DeviceInterfaceData { Size = Marshal.SizeOf<DeviceInterfaceData>() };
                    if (!SetupDiEnumDeviceInterfaces(devices, 0, ref guid, index, ref device))
                    {
                        complete &= Marshal.GetLastWin32Error() == ErrorNoMoreItems;
                        break;
                    }
                    var path = ReadInterfacePath(devices, ref device);
                    if (path is null) { complete = false; continue; }
                    // These IOCTLs are FILE_ANY_ACCESS; no elevation or write access is needed.
                    using var hub = CreateFile(path, 0, 3, 0, 3, 0, 0);
                    if (hub.IsInvalid) { complete = false; continue; }
                    var info = new byte[76]; // USB_NODE_INFORMATION, NodeType = UsbHub (0)
                    if (!Query(hub, GetNodeInformation, info, 7)) { complete = false; continue; }
                    var portCount = info[6]; // HubDescriptor.bNumberOfPorts
                    for (uint port = 1; port <= portCount; port++)
                    {
                        var connection = ReadConnection(hub, port);
                        if (connection is null) { complete = false; continue; }
                        if (!IsValveDongle(connection)) continue;
                        var extended = new byte[16];
                        BinaryPrimitives.WriteUInt32LittleEndian(extended, port);
                        BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(4), 16);
                        BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(8), 7); // Caller understands USB 1/2/3.
                        uint? flags = Query(hub, GetConnectionInformationExV2, extended, 16)
                            ? BinaryPrimitives.ReadUInt32LittleEndian(extended.AsSpan(12)) : null;
                        // A detach/reconnect between the two IOCTLs must not pair different devices.
                        var confirmed = ReadConnection(hub, port);
                        if (confirmed is null || !IsValveDongle(confirmed)) { complete = false; continue; }
                        links.Add(new(DecodeSpeed(confirmed[23], flags)));
                    }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(devices); }
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException or UnauthorizedAccessException)
        {
            complete = false;
        }
        return new(links, complete, DateTimeOffset.Now);
    }

    internal static bool IsValveDongle(ReadOnlySpan<byte> data) => data.Length >= ConnectionInformationSize
        && BinaryPrimitives.ReadUInt32LittleEndian(data[31..]) == 1 // DeviceConnected
        && data[4] == 18 && data[5] == 1 // USB_DEVICE_DESCRIPTOR
        && BinaryPrimitives.ReadUInt16LittleEndian(data[12..]) == 0x28de
        && BinaryPrimitives.ReadUInt16LittleEndian(data[14..]) == 0x2432;

    internal static UsbDongleSpeed DecodeSpeed(byte speed, uint? flags)
    {
        // EX can report HighSpeed even for SuperSpeed devices. V2's *operating* flags,
        // not its capability flags or SupportedUsbProtocols, establish USB 3.
        if (flags is uint value)
        {
            if ((value & 4) != 0) return UsbDongleSpeed.SuperSpeedPlus;
            if ((value & 1) != 0) return UsbDongleSpeed.SuperSpeed;
        }
        return speed switch
        {
            0 => UsbDongleSpeed.LowSpeed,
            1 => UsbDongleSpeed.FullSpeed,
            2 when flags.HasValue => UsbDongleSpeed.HighSpeed,
            3 => UsbDongleSpeed.SuperSpeed,
            _ => UsbDongleSpeed.Unknown
        };
    }

    private static byte[]? ReadConnection(SafeFileHandle hub, uint port)
    {
        // Extra space permits the variable USB_PIPE_INFO array without relying on its contents.
        var data = new byte[ConnectionInformationSize + 32 * 11];
        BinaryPrimitives.WriteUInt32LittleEndian(data, port);
        return Query(hub, GetConnectionInformationEx, data, ConnectionInformationSize) ? data : null;
    }

    private static bool Query(SafeFileHandle hub, uint code, byte[] data, int minimumSize) =>
        DeviceIoControl(hub, code, data, data.Length, data, data.Length, out var written, 0) && written >= minimumSize;

    private static string? ReadInterfacePath(nint devices, ref DeviceInterfaceData device)
    {
        SetupDiGetDeviceInterfaceDetail(devices, ref device, 0, 0, out var required, 0);
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || required < 6 || required > 65536) return null;
        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            // cbSize includes native alignment; DevicePath starts at offset 4 on both architectures.
            Marshal.WriteInt32(buffer, nint.Size == 8 ? 8 : 6);
            return SetupDiGetDeviceInterfaceDetail(devices, ref device, buffer, required, out _, 0)
                ? Marshal.PtrToStringUni(buffer + 4) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint devices, nint deviceInfo, ref Guid classGuid, uint index, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint devices, ref DeviceInterfaceData data, nint detail, int size, out int required, nint deviceInfo);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint devices);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, [In, Out] byte[] input, int inputSize,
        [In, Out] byte[] output, int outputSize, out int returned, nint overlapped);
}
