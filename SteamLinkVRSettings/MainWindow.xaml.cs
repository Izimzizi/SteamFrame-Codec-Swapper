using Microsoft.UI.Xaml;
using SteamLinkVRSettings.Views;

namespace SteamLinkVRSettings;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        Title = "Steam Link VR Settings";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900));

        ContentFrame.Navigate(typeof(SettingsPage));
    }
}
