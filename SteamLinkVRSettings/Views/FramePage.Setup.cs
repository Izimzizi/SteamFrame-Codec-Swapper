using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private async Task ShowFirstRunSetupAsync(bool force = false)
    {
        if (!force && _preferences.BitrateSetupVersion >= BitrateSetup.Version) return;
        // Inspect the discovered runtime, not a preset flag or a previous app's preferences.
        _patch = await _patcher.InspectAsync(_paths.Driver); BindPatch();
        var install = BitrateSetup.CanInstall(_patch);
        var panel = new StackPanel { Spacing = 14, MaxWidth = 480 };
        panel.Children.Add(new TextBlock { Text = "1. Check your SteamVR driver", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = BitrateSetup.Explanation(_patch), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "2. Enable higher bitrate requests", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = install
            ? "Install saves and verifies the original driver, applies the override, then verifies the result. SteamVR will close first and restart if it was running; your game may close too."
            : "Opening the app or selecting a preset does not patch a driver. Applying a preset that needs the override can install it. The verified file state is always shown under Patches & Recovery.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "3. Choose your stream settings", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "After setup, load a bundled preset or run Auto tune. Higher Mbps is a requested ceiling; watch measured video and frame timing. SteamVR updates can replace this driver, so check its status again after an update.", TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Set up higher bitrate streaming", Content = new ScrollViewer { Content = panel, MaxHeight = 440 },
            PrimaryButtonText = install ? "Install 500 Mbps override" : "Continue", CloseButtonText = "Set up later", DefaultButton = ContentDialogButton.Close };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Primary && install)
        {
            var succeeded = false;
            await ActionAsync(async () =>
            {
                await EnsureReconnectOwnerAsync();
                var resume = await Task.Run(() => OpenVrSettings.IsRunning);
                try
                {
                    if (resume) await SteamVrRuntime.StopAsync(_paths);
                    _patch = await _patcher.ApplyAsync(_paths.Driver); BindPatch();
                    if (!_patch.IsPatched) throw new IOException("The bitrate override was not verified. Check Patches & Recovery.");
                    _preferences.BitrateSetupVersion = BitrateSetup.Version; await _preferences.SaveAsync();
                    succeeded = true;
                }
                finally { if (resume) SteamVrRuntime.Start(_paths); }
                return "500 Mbps override installed and verified. Choose a preset in Stream settings or run Auto tune.";
            });
            if (!succeeded) return; // Keep first-run guidance pending after a failed write/elevation.
        }
        else { _preferences.BitrateSetupVersion = BitrateSetup.Version; await _preferences.SaveAsync(); }
        SectionRequested?.Invoke(choice == ContentDialogResult.Primary ? "stream" : "tools");
    }

    private async void BitrateSetup_Click(object sender, RoutedEventArgs e)
    {
        if (_actionPending) return;
        try { await ShowFirstRunSetupAsync(force: true); }
        catch (Exception error) { ShowStatus(error.Message, true); }
    }
}
