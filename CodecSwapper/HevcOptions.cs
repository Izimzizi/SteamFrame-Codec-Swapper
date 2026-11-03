using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>
/// The encoder options for Default (HEVC), as hevc-options.txt in the tray's data folder. The driver hook reads the file about twice a second and
/// applies it to SteamVR's own encoder, so the same rate control, preset and VBV as H.264 apply to Default (HEVC). Without the file the stock stream is left as SteamVR configures it.
/// </summary>
public static class HevcOptions
{
    public static string Text(FramePreferences preferences) =>
        $"enabled=1\nrate_control={(preferences.HookRateControl == "cbr" ? "cbr" : "vbr")}\npreset={Math.Clamp(preferences.HookPreset, 1, 7)}\nvbv={Math.Clamp(preferences.HookVbvFrames, 1, 16)}\nmultipass={Math.Clamp(preferences.HookMultiPass, 0, 2)}\ntarget={Math.Clamp(preferences.CodecTargetMbps, 1, 1000)}\n";

    public static void Write(string path, FramePreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Text(preferences));
        File.Move(temporary, path, overwrite: true);
    }
}
