using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamLinkVRSettings.Views;
using Windows.Graphics;

namespace SteamLinkVRSettings;

public sealed partial class MainWindow : Window
{
    private const int PreferredWidth = 1180, PreferredHeight = 940, MinimumWidth = 720, MinimumHeight = 560;
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    private static string WindowStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamLinkVRSettings", "window.json");

    private FramePage? _framePage;
    private NavigationViewItem? _currentItem;
    private bool _suppressSelection;
    private bool _closeApproved;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        Title = Elevation.IsElevated ? "SteamFrame Advanced Options (Administrator)" : "SteamFrame Advanced Options";
        TitleText.Text = Title;
        RestoreWindowSize();

        AppWindow.Closing += AppWindow_Closing;
        Elevation.RequestShutdownAsync = () => { DispatcherQueue.TryEnqueue(Close); return Task.CompletedTask; };

        _currentItem = (NavigationViewItem)Navigation.MenuItems[0];
        Navigation.SelectedItem = _currentItem;
    }

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressSelection || args.SelectedItem is not NavigationViewItem item) return;
        if (ContentFrame.Content is SettingsPage settings && !ReferenceEquals(item, _currentItem) && settings.HasUnsavedChanges)
        {
            // Keep the editor visible until the user decides what to do with the changes.
            SelectWithoutNavigating(_currentItem);
            if (!await ResolveUnsavedSettingsAsync(settings)) return;
            SelectWithoutNavigating(item);
        }
        NavigateTo(item);
    }

    private void NavigateTo(NavigationViewItem item)
    {
        _currentItem = item;
        var page = item.Tag as string == "settings" ? typeof(SettingsPage) : typeof(FramePage);
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
        if (ContentFrame.Content is FramePage frame)
        {
            if (!ReferenceEquals(_framePage, frame)) frame.SectionRequested += NavigateToSection;
            _framePage = frame;
            frame.ShowSection(item.Tag as string ?? "overview");
        }
    }

    private void NavigateToSection(string section)
    {
        var item = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(x => x.Tag as string == section);
        if (item != null) Navigation.SelectedItem = item;
    }

    private void SelectWithoutNavigating(NavigationViewItem? item)
    {
        _suppressSelection = true;
        try { Navigation.SelectedItem = item; }
        finally { _suppressSelection = false; }
    }

    /// <summary>Asks to save or discard unsaved settings. Returns false when the user cancels.</summary>
    private async Task<bool> ResolveUnsavedSettingsAsync(SettingsPage settings)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Save changes to streaming settings?",
            Content = "You have unsaved changes in Additional settings.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => await settings.SaveAsync(),
            ContentDialogResult.Secondary => true,
            _ => false
        };
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        try
        {
            if (ContentFrame.Content is SettingsPage settings && settings.HasUnsavedChanges && !await ResolveUnsavedSettingsAsync(settings))
                return;

            // Let a running patch, apply or monitor operation finish, then release
            // connection resources before the process exits.
            if (_framePage is { IsBusy: true }) TitleText.Text = Title + " — finishing the current operation…";
            if (_framePage is not null) await _framePage.CloseAsync();

            SaveWindowSize();
            _closeApproved = true;
            Close();
        }
        catch (Exception error)
        {
            await new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Could not finish closing",
                Content = error.Message + " The window remains open so you can retry.",
                CloseButtonText = "OK"
            }.ShowAsync();
        }
        finally
        {
            _closing = false;
            if (!_closeApproved) TitleText.Text = Title;
        }
    }

    private void RestoreWindowSize()
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi > 0 ? dpi / 96.0 : 1;
        int width = (int)(PreferredWidth * scale), height = (int)(PreferredHeight * scale);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(WindowStatePath));
            width = document.RootElement.GetProperty("width").GetInt32();
            height = document.RootElement.GetProperty("height").GetInt32();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }

        // Never open larger than the screen the window lands on (laptops at 125–150% scaling).
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        width = Math.Clamp(width, Math.Min((int)(MinimumWidth * scale), work.Width), work.Width);
        height = Math.Clamp(height, Math.Min((int)(MinimumHeight * scale), work.Height), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
    }

    private void SaveWindowSize()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return;
            Directory.CreateDirectory(Path.GetDirectoryName(WindowStatePath)!);
            File.WriteAllText(WindowStatePath, JsonSerializer.Serialize(new { width = AppWindow.Size.Width, height = AppWindow.Size.Height }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
