namespace SteamLinkVRSettings.Frame;

/// <summary>
/// Opens a JSONL diagnostic capture for appending. A record cut short by a crash or power loss
/// is terminated first, so the next record starts on its own line instead of merging into it.
/// </summary>
internal static class CaptureFile
{
    public static FileStream OpenAppend(string path)
    {
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            if (stream.Length > 0)
            {
                stream.Position = stream.Length - 1;
                if (stream.ReadByte() != '\n') { stream.Position = stream.Length; stream.WriteByte((byte)'\n'); }
            }
            stream.Position = stream.Length;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
}
