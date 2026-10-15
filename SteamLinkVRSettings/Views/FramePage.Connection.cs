using Microsoft.UI.Xaml.Media;
using SteamLinkVRSettings.Frame;
using Windows.UI;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private Task<UsbDongleSnapshot>? _usbDongleRead;
    private UsbDongleSnapshot _usbDongle = UsbDongleSnapshot.Unknown;
    private UsbDongleSnapshot _usbCurrent = UsbDongleSnapshot.Unknown;
    private double _nextUsbDongleRead;

    private void PollUsbDongle(double at)
    {
        if (_usbDongleRead is { IsCompleted: true })
        {
            _usbDongle = _usbDongleRead.IsCompletedSuccessfully ? _usbDongleRead.Result : UsbDongleSnapshot.Unknown;
            _ = _usbDongleRead.Exception;
            _usbDongleRead = null;
        }
        if (_usbDongleRead is null && at >= _nextUsbDongleRead)
        {
            _usbDongleRead = Task.Run(UsbDongleConnection.Read);
            _nextUsbDongleRead = at + 5;
        }
        // A stalled hub query must neither block the graph nor leave an old USB 3 result visible.
        var current = DateTimeOffset.Now - _usbDongle.ObservedAt <= TimeSpan.FromSeconds(15)
            ? _usbDongle : UsbDongleSnapshot.Unknown;
        _usbCurrent = current;
        DongleUsbReadback.Text = current.Summary;
        DongleUsbReadback.Foreground = new SolidColorBrush(current.HasSlowConnection
            ? Color.FromArgb(255, 255, 185, 83) : Color.FromArgb(255, 200, 200, 208));
        DongleUsbDetail.Text = current.Detail;
        ConnectionUsbStatus.Text = current.Summary + "\n" + current.Detail;
        DiagnosticUsbStatus.Text = current.Summary + (current.HasSlowConnection ? " — bandwidth limited" : "");
    }
}
