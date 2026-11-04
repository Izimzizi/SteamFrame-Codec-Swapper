namespace CodecSwapper;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (AskPass.TryAnswer(args)) return;
        if (args.Contains(DashboardNudge.Argument)) { ApplicationConfiguration.Initialize(); Environment.ExitCode = DashboardNudge.Run(); return; }
        if (args.Contains("--hud-host")) { Environment.ExitCode = HudHost.Run(); return; }
        using var single = new Mutex(true, @"Local\CodecSwapper.SingleInstance", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => { LogCrash("UI thread", e.Exception); MessageBox.Show(e.Exception.Message, "SteamFrame Codec Swapper", MessageBoxButtons.OK, MessageBoxIcon.Error); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash("unobserved task", e.Exception); e.SetObserved(); };
        Application.Run(new TrayApp(autostart: args.Contains("--autostart")));
    }

    /// <summary>Failures that would otherwise vanish (a background task nobody awaited, a crash on a worker thread) are written to tray.log so a bug report has something to show.</summary>
    private static void LogCrash(string where, Exception? error)
    {
        try { File.AppendAllText(Path.Combine(AppData.Root, "tray.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {where}: {error}{Environment.NewLine}"); }
        catch (Exception) { }
    }
}
