using SteamFrame.Core;

namespace SteamFrameTray;

/// <summary>
/// The encoder options for Default (HEVC), as stock-options.txt in the tray's data folder. The driver hook reads the file about twice a second and
/// applies it to SteamVR's own encoder; with "enabled=0" (the default) the stock stream is left exactly as SteamVR configures it.
/// </summary>
public static class StockOptions
{
    public static string Text(FramePreferences preferences) =>
        $"enabled={(preferences.HookApplyToHevc ? 1 : 0)}\nrate_control={(preferences.HookRateControl == "cbr" ? "cbr" : "vbr")}\npreset={Math.Clamp(preferences.HookPreset, 1, 7)}\nvbv={Math.Clamp(preferences.HookVbvFrames, 1, 16)}\n";

    public static void Write(string path, FramePreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Text(preferences));
        File.Move(temporary, path, overwrite: true);
    }
}
