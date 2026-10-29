using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SteamFrame.Core;

public sealed record FrameLink(int Type, bool Active, double LatencyMs, double LossRatio)
{
    public string Name => Type switch { 1 => "USB cable", 2 => "Dedicated wireless", 3 => "Local network", 255 => "Combined", _ => "Other" };
}

public sealed record ConnectionSnapshot(bool? WifiEnabled, bool HasInfo, IReadOnlyList<FrameLink> Links, string Message)
{
    public bool WiredActive => HasInfo && Links.Any(l => l.Type == 1 && l.Active);
    public FrameLink[] ActiveLinks => HasInfo ? Links.Where(l => l.Active && l.Type != 255).ToArray() : [];
    public string VideoPath => !HasInfo ? "Unknown" : ActiveLinks.Length switch
    {
        0 => "Inactive", 1 => ActiveLinks[0].Name, _ => "Multiple links"
    };
}

public sealed record AdapterSample(string Name, string Address, bool Up, double? SendMbps, double? ReceiveMbps, int LinkType);

public sealed class FrameTrafficMonitor
{
    private readonly Dictionary<string, (long Sent, long Received, long At)> _previous = [];

    internal static int? Classify(string description)
    {
        if (description.Contains("Filter", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Scheduler", StringComparison.OrdinalIgnoreCase)) return null;
        if (description.Contains("UsbNcm", StringComparison.OrdinalIgnoreCase)) return 1;
        if ((description.Contains("Valve", StringComparison.OrdinalIgnoreCase) || description.Contains("Steam Frame", StringComparison.OrdinalIgnoreCase)) &&
            (description.Contains("Wireless", StringComparison.OrdinalIgnoreCase) || description.Contains("WiFi", StringComparison.OrdinalIgnoreCase))) return 2;
        return null;
    }

    // Only graph a unique adapter for a unique headset-confirmed route. Presence
    // alone cannot establish the stream path; different links may duplicate traffic.
    internal static double? ActiveSendMbps(ConnectionSnapshot links, IReadOnlyList<AdapterSample> adapters)
    {
        var active = links.ActiveLinks;
        if (active.Length != 1) return null;
        var candidates = adapters.Where(a => a.LinkType == active[0].Type && a.Up && a.Address != "No IPv4 address").ToArray();
        return candidates.Length == 1 ? candidates[0].SendMbps : null;
    }

    public IReadOnlyList<AdapterSample> Sample()
    {
        var result = new List<AdapterSample>();
        var seen = new HashSet<string>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (Classify(adapter.Description) is not int type) continue;
            seen.Add(adapter.Id);
            try
            {
                var at = Stopwatch.GetTimestamp();
                var stats = adapter.GetIPStatistics();
                double? send = null, receive = null;
                if (_previous.TryGetValue(adapter.Id, out var last) && stats.BytesSent >= last.Sent && stats.BytesReceived >= last.Received)
                {
                    var seconds = (at - last.At) / (double)Stopwatch.Frequency;
                    if (seconds > 0.1)
                    {
                        send = (stats.BytesSent - last.Sent) * 8.0 / seconds / 1e6;
                        receive = (stats.BytesReceived - last.Received) * 8.0 / seconds / 1e6;
                    }
                }
                _previous[adapter.Id] = (stats.BytesSent, stats.BytesReceived, at);
                var ip = adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "No IPv4 address";
                result.Add(new(adapter.Name, ip, adapter.OperationalStatus == OperationalStatus.Up, send, receive, type));
            }
            catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or UnauthorizedAccessException)
            {
                _previous.Remove(adapter.Id);
            }
        }
        foreach (var id in _previous.Keys.Except(seen).ToArray()) _previous.Remove(id);
        return result;
    }
}
