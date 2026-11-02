namespace CodecSwapper;

/// <summary>
/// The foveation quality bias as foveation.txt in the tray's data folder. The driver hook reads it about twice a second and adds the offsets to
/// SteamVR's quality map for both codecs. Both zero removes the file, which leaves the map exactly as SteamVR made it.
/// </summary>
public static class FoveationBias
{
    public static string Text(int gaze, int periphery) => $"gaze={Math.Clamp(gaze, -20, 12)}\nperiphery={Math.Clamp(periphery, -12, 31)}\n";

    public static void Write(string path, int gaze, int periphery)
    {
        try
        {
            if (gaze == 0 && periphery == 0) { File.Delete(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Text(gaze, periphery));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
