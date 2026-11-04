using System.Runtime.InteropServices;
using System.Text;
using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>
/// Brings back the Frame's missing dashboard windows. After a codec switch the PC sometimes does not send the Frame the desktop and Steam windows; adding
/// any window with the dashboard's + makes the PC send them all again. This does what + does: it shows a small window of its own, asks SteamVR's desktop
/// view (through SteamVR's internal mailbox, IVRMailbox_002) to spawn a window view for it, and closes the window again a moment later. It runs as a short
/// separate process (<c>--dashboard-nudge</c>) so its OpenVR connection never lives in the tray.
/// </summary>
internal static unsafe class DashboardNudge
{
    public const string Argument = "--dashboard-nudge";
    private const string Interface = "IVRMailbox_002";

    /// <summary>0 = done; 1 = SteamVR is not running; 2 = SteamVR did not accept the connection; 3 = the mailbox is not what this was built for; 4 = the message was refused.</summary>
    public static int Run()
    {
        if (System.Diagnostics.Process.GetProcessesByName("vrserver").Length == 0) return 1;
        var library = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad(Path.Combine(SteamPaths.Current.SteamVrRoot, "bin", "win64", "openvr_api.dll"), out library)) return 2;
            var init = (delegate* unmanaged<int*, int, uint>)NativeLibrary.GetExport(library, "VR_InitInternal");
            var shutdown = (delegate* unmanaged<void>)NativeLibrary.GetExport(library, "VR_ShutdownInternal");
            var get = (delegate* unmanaged<byte*, int*, nint>)NativeLibrary.GetExport(library, "VR_GetGenericInterface");
            int code = 0;
            init(&code, 2);
            if (code != 0) return 2;
            try { return Send(get); }
            finally { try { shutdown(); } catch (Exception) { /* SteamVR went away */ } }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or AccessViolationException)
        {
            return 3;
        }
        finally { if (library != IntPtr.Zero) NativeLibrary.Free(library); }
    }

    private static int Send(delegate* unmanaged<byte*, int*, nint> get)
    {
        int error = 0;
        nint mailbox;
        fixed (byte* name = Ascii(Interface)) mailbox = get(name, &error);
        if (mailbox == 0 || error != 0) return 3;
        var table = *(nint**)mailbox;   // 0 open, 1 close, 2 send
        ulong handle = 0;
        int result;
        fixed (byte* own = Ascii("codecswapper_nudge")) result = ((delegate* unmanaged<nint, byte*, ulong*, byte*, uint, int>)table[0])(mailbox, own, &handle, null, 0);
        if (result != 0 || handle == 0) return 3;
        try
        {
            using var window = new NudgeWindow();
            window.Show();
            Pump(window, 1500);
            var message = $"{{\"type\":\"request_spawn_window_view\",\"hwnd\":\"{window.Handle.ToInt64()}\"}}";
            int sent;
            fixed (byte* to = Ascii("desktopview")) fixed (byte* text = Ascii(message))
                sent = ((delegate* unmanaged<nint, ulong, byte*, byte*, int>)table[2])(mailbox, handle, to, text);
            Pump(window, 2000);
            window.Close();
            return sent == 0 ? 0 : 4;
        }
        finally { ((delegate* unmanaged<nint, ulong, int>)table[1])(mailbox, handle); }
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text + "\0");

    private static void Pump(Form window, int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(30); }
    }

    /// <summary>A small ordinary window in the screen's lower right corner that does not take the focus from a running game.</summary>
    private sealed class NudgeWindow : Form
    {
        public NudgeWindow()
        {
            Text = "SteamFrame Codec Swapper";
            // An ordinary window: SteamVR's desktop view lists windows like the taskbar does, and a tool window (small caption, no taskbar button) is left out,
            // which is why the first version of this nudge ended with exit code 0 and no windows back. The manual test that worked used a normal window.
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
            Size = new Size(240, 70);
            Location = new Point(area.Right - Width, area.Bottom - Height);
        }

        protected override bool ShowWithoutActivation => true;
    }
}
