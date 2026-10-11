using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SteamLinkVRSettings;

/// <summary>
/// The app runs as the signed-in user. Only operations that hit a protected location ask to
/// restart elevated, so HKCU/LocalAppData keep pointing at the user who runs Steam.
/// </summary>
public static class Elevation
{
    public static bool IsElevated { get; } = CheckElevated();

    /// <summary>Raised before relaunching; the window should close gracefully when it returns true.</summary>
    public static Func<Task>? RequestShutdownAsync { get; set; }

    private static bool CheckElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool IsAccessDenied(Exception error) => error switch
    {
        UnauthorizedAccessException => true,
        Win32Exception { NativeErrorCode: 5 } => true,
        IOException io => io.HResult == unchecked((int)0x80070005),
        _ => error.InnerException is not null && IsAccessDenied(error.InnerException)
    };

    /// <summary>Starts an elevated copy and closes this one. Returns false if the user declined UAC.</summary>
    public static async Task<bool> RestartElevatedAsync()
    {
        var path = Environment.ProcessPath;
        if (path is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory })?.Dispose();
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC declined.
        {
            return false;
        }
        if (RequestShutdownAsync is { } shutdown) await shutdown();
        return true;
    }

    public static Button CreateRestartButton(XamlRoot? root)
    {
        var button = new Button { Content = "Restart as administrator" };
        button.Click += async (_, _) => await RestartElevatedAsync();
        return button;
    }
}
