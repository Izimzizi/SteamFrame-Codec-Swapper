using System.Text.Json;
using System.Text.Json.Nodes;
using SteamFrame.Core;

namespace CodecSwapper;

/// <summary>
/// SteamVR's stream width (<c>driver_vrlink.streamFormatWidth</c>): the width of each region of the packed picture. SteamLink's own
/// slider runs 960 to 1344; the driver takes more. The tall stack is four widths high and NVENC HEVC stops at 8192 rows, so 2048 is
/// the most HEVC can take. H.264 stays at 1024 or less: while it is the codec the hook lowers the width and puts the user's own back.
/// </summary>
public static class StreamWidth
{
    public const int Minimum = 960, Maximum = 2048, Step = 64, H264Limit = 1024;
    private const string Key = "streamFormatWidth", Automatic = "automaticStreamFormatWidth";

    /// <summary>Rounds to the multiple of 64 (the driver needs 32; SteamLink steps by 64), inside the slider's range.</summary>
    public static int Snapped(int width) => Math.Clamp((int)Math.Round(width / (double)Step) * Step, Minimum, Maximum);

    /// <summary>Where the hook keeps the user's own width while H.264 has lowered it ("width automatic").</summary>
    public static string RestorePath => Path.Combine(AppData.Root, "width-restore.txt");

    /// <summary>The width to show: the user's own while H.264 holds a lowered one, otherwise SteamVR's live value (or its settings file).</summary>
    public static Task<int?> ReadAsync(SteamPaths paths) => ReadAsync(paths, RestorePath);

    public static Task<int?> ReadAsync(SteamPaths paths, string restorePath) => Task.Run(() =>
    {
        if (ReadRestore(restorePath) is { } pending) return (int?)pending.Width;
        if (OpenVrSettings.IsRunning)
        {
            try { return OpenVrSettings.Read(paths).StreamWidth; }
            catch (Exception error) when (error is IOException or InvalidOperationException) { /* the file below is the fallback */ }
        }
        return ReadFile(paths.Config);
    });

    /// <summary>Applies live through SteamVR when it runs (it re-lays the stream out at once), otherwise edits its settings file.</summary>
    public static Task ApplyAsync(SteamPaths paths, int width) => ApplyAsync(paths, width, RestorePath);

    /// <summary>
    /// While H.264 is the codec the stored width is the lowered one and the user's own waits in the restore file: the new choice goes
    /// there (it is what comes back with HEVC), and SteamVR itself gets it only as far as H.264 takes it.
    /// </summary>
    public static Task ApplyAsync(SteamPaths paths, int width, string restorePath) => Task.Run(() =>
    {
        width = Snapped(width);
        var live = width;
        if (ReadRestore(restorePath) is not null)
        {
            WriteRestore(restorePath, width);
            live = Math.Min(width, H264Limit);
        }
        if (OpenVrSettings.IsRunning)
            OpenVrSettings.Apply(paths, new Dictionary<string, object?> { [Automatic] = false, [Key] = live });
        else WriteFile(paths.Config, live);
    });

    /// <summary>The restore file the hook writes when H.264 lowers the width: the user's width and whether it was automatic.</summary>
    public static (int Width, int Automatic)? ReadRestore(string path)
    {
        try
        {
            var parts = File.ReadAllText(path).Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var automatic) && width > 0 ? (width, automatic) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteRestore(string path, int width)
    {
        var automatic = ReadRestore(path)?.Automatic ?? 0;
        File.WriteAllText(path, $"{width} {automatic}{Environment.NewLine}");
    }

    public static int? ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.TryGetProperty("driver_vrlink", out var section) && section.ValueKind == JsonValueKind.Object &&
                   section.TryGetProperty(Key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var width) ? width : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Sets the width (and turns automatic width off) in the settings file, keeping every other key. For use while SteamVR is closed.</summary>
    public static void WriteFile(string path, int width)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            try { root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?.AsObject() ?? throw new JsonException(); }
            catch (JsonException error) { throw new IOException("SteamVR's settings file could not be read, so it was left unchanged.", error); }
        }
        else root = new JsonObject();
        if (root["driver_vrlink"] is not JsonObject section) root["driver_vrlink"] = section = new JsonObject();
        section[Automatic] = false;
        section[Key] = width;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tray-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
