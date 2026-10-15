using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    public event Action<string>? SectionRequested;
    public void ShowSection(string section)
    {
        foreach (var panel in new FrameworkElement[] { OverviewView, StreamView, BenchmarkView, DiagnosticsView, AdvancedView, ConnectionView, ToolsView, StockView }) panel.Visibility = Visibility.Collapsed;
        var (view, heading, description) = section switch
        {
            "stream" => ((FrameworkElement)StreamView, "Stream settings", "One setup for codec, bitrate and picture quality."),
            "benchmark" => (BenchmarkView, "Auto tune", "Find a wireless HEVC preset for this PC and your Frame."),
            "diagnostics" => (DiagnosticsView, "Diagnostics", "Find when and where the stream runs into trouble."),
            "advanced" => (AdvancedView, "Advanced", "Change individual encoder and driver behaviors."),
            "connection" => (ConnectionView, "Connection", "Pair developer tools and check the headset connection."),
            "tools" => (ToolsView, "Patches & Recovery", "Inspect the bitrate override and restore original components."),
            "stock" => (StockView, "Back to stock", "Recover a working baseline without losing your tuning."),
            _ => (OverviewView, "Overview", "Your live stream, at a glance.")
        };
        view.Visibility = Visibility.Visible; SectionHeading.Text = heading; SectionDescription.Text = description;
        if (section == "stock") _ = RefreshStockStatusAsync();
    }
    private void OpenSection_Click(object sender, RoutedEventArgs e) => SectionRequested?.Invoke((sender as FrameworkElement)?.Tag as string ?? "overview");
    private void UseDongleAddress_Click(object sender, RoutedEventArgs e) => SshHostInput.Text = "10.35.78.1";
    private void OverviewMetrics_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 610;
        OverviewMetrics.ColumnDefinitions[1].Width = OverviewMetrics.ColumnDefinitions[2].Width = wide ? GridLength.Auto : new GridLength(0);
        Grid.SetRow(TargetMetric, wide ? 0 : 1); Grid.SetColumn(TargetMetric, wide ? 1 : 0);
        Grid.SetRow(PathMetric, wide ? 0 : 2); Grid.SetColumn(PathMetric, wide ? 2 : 0);
        TargetMetric.Margin = PathMetric.Margin = wide ? new Thickness(0, 5, 0, 0) : new Thickness(0, 16, 0, 0);
    }
    private static void PairFields(Grid grid, FrameworkElement second, double width)
    {
        var wide = width >= 500;
        grid.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(second, wide ? 0 : 1); Grid.SetColumn(second, wide ? 1 : 0);
    }
    private void StreamMainFields_SizeChanged(object sender, SizeChangedEventArgs e) => PairFields(StreamMainFields, CodecRefreshInput, e.NewSize.Width);
    private void GazeFields_SizeChanged(object sender, SizeChangedEventArgs e) => PairFields(GazeFields, PeripheralInput, e.NewSize.Width);
}
