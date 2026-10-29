namespace SteamFrameTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (AskPass.TryAnswer(args)) return;
        using var single = new Mutex(true, @"Local\SteamFrameTray.SingleInstance", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "SteamFrame Tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new TrayApp(autostart: args.Contains("--autostart")));
    }
}
