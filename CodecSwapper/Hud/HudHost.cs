using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>
/// The HUD runs in its own process (CodecSwapper.exe --hud-host): it talks to SteamVR's native overlay API, and a native fault there
/// must never take the tray down. The tray sends it the readouts and settings; it sends back button presses and the saved placement.
/// </summary>
public static class HudHost
{
    public static int Run()
    {
        var input = new StreamReader(Console.OpenStandardInput());
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var gate = new object();
        void Send(HudMessage message) { lock (output) { try { output.WriteLine(message.ToLine()); } catch (IOException) { /* the tray is gone */ } } }
        void Log(string line) => Send(new() { Type = "log", Line = line });

        var settings = new HudSettings();
        var view = HudView.Empty;
        var quit = false;
        var actions = new HostActions(Send);
        long checkedAt = 0; var running = false;
        bool PcVrRunning()
        {
            if (Environment.TickCount64 - checkedAt >= 1000) { checkedAt = Environment.TickCount64; running = OpenVrOverlayApi.IsVrServerRunning(); }
            return running;
        }
        var hud = new HudController(new OpenVrOverlayApi(SteamPaths.Current.SteamVrRoot, Log, Path.Combine(AppData.Root, "HudInput")), settings, () => { lock (gate) return view; }, actions,
            () => Send(new() { Type = "settings", Settings = settings }), PcVrRunning, Log);

        var reader = new Thread(() =>
        {
            string? line;
            while ((line = input.ReadLine()) is not null)
            {
                var message = HudMessage.Parse(line);
                if (message is null) continue;
                lock (gate)
                {
                    if (message.Type == "view" && message.View is not null) view = message.View;
                    else if (message.Type == "settings" && message.Settings is not null)
                    {
                        settings.Enabled = message.Settings.Enabled; settings.Hand = message.Settings.Hand; settings.FlipLaserY = message.Settings.FlipLaserY;
                        settings.AutoShow = message.Settings.AutoShow; settings.ExpandOnHover = message.Settings.ExpandOnHover; settings.Watch = message.Settings.Watch; settings.FadeAngle = message.Settings.FadeAngle; settings.FadeDistanceCm = message.Settings.FadeDistanceCm; settings.GuideDone = message.Settings.GuideDone; settings.OwnLaser = message.Settings.OwnLaser;
                        // The helper owns the placement while the user is moving the HUD.
                        if (!hud.Repositioning) { settings.Placement = message.Settings.Placement; settings.WidthMeters = message.Settings.WidthMeters; settings.IconPlacement = message.Settings.IconPlacement; settings.IconWidthMeters = message.Settings.IconWidthMeters; }
                    }
                    else if (message.Type == "dashboard") hud.RequestDashboardRefresh();
                    else if (message.Type == "quit") quit = true;
                }
            }
            quit = true; // the tray closed the pipe or died
        }) { IsBackground = true };
        reader.Start();

        Log("HUD helper started");
        while (!quit)
        {
            lock (gate)
            {
                try { hud.Tick(); } catch (Exception error) when (error is not OutOfMemoryException) { Log("HUD tick failed: " + error.Message); hud.Teardown(); }
            }
            Thread.Sleep(16);
        }
        hud.Dispose();
        return 0;
    }

    private sealed class HostActions(Action<HudMessage> send) : IHudActions
    {
        public Task SetCodecAsync(bool h264) { send(new() { Type = "action", Kind = "codec", H264 = h264 }); return Task.CompletedTask; }
        public Task SetBitrateAsync(int mbps) { send(new() { Type = "action", Kind = "bitrate", Mbps = mbps }); return Task.CompletedTask; }
        public Task SetWidthAsync(int width) { send(new() { Type = "action", Kind = "width", Width = width }); return Task.CompletedTask; }
        public Task HideAsync() { send(new() { Type = "action", Kind = "hide" }); return Task.CompletedTask; }
        public Task ResetCountersAsync() { send(new() { Type = "action", Kind = "counters" }); return Task.CompletedTask; }
        public Task SetMultiPassAsync(int mode) { send(new() { Type = "action", Kind = "multipass", MultiPass = mode }); return Task.CompletedTask; }
        public Task SetEncoderAsync(string rateControl, int preset, int vbvFrames)
        {
            send(new() { Type = "action", Kind = "encoder", RateControl = rateControl, Preset = preset, VbvFrames = vbvFrames });
            return Task.CompletedTask;
        }
    }
}
