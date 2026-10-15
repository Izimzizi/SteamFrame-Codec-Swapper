using System.Diagnostics;
using System.Text;

namespace SteamLinkVRSettings.Frame;

internal static class FrameSsh
{
    public static async Task<string> ExecuteAsync(FramePreferences preferences, string script, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = Process.Start(FrameReceiverDiagnostics.CreateSshStartInfo(preferences)) ?? throw new IOException("Could not start the paired Frame connection.");
        var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var errors = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            await process.StandardInput.WriteAsync(script.AsMemory(), deadline.Token); process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, errors);
            if (process.ExitCode != 0) throw new IOException("Frame request failed. Check the paired SSH connection and keep the headset awake.");
            return await output;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new IOException("The Frame did not respond within eight seconds. Check paired SSH and keep the headset awake.", error); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(output, errors); } catch (Exception) { }
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer, token)) != 0)
        { if (text.Length + count > 16384) throw new IOException("Headset response exceeded the metadata limit."); text.Append(buffer, 0, count); }
        return text.ToString();
    }
}
