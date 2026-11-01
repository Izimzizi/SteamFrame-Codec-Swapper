namespace CodecSwapper;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (AskPass.TryAnswer(args)) return;
        if (args.Contains("--hud-host")) { Environment.ExitCode = HudHost.Run(); return; }
        using var single = new Mutex(true, @"Local\CodecSwapper.SingleInstance", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "SteamFrame Codec Swapper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new TrayApp(autostart: args.Contains("--autostart")));
    }
}
