using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamFrame.Core;

internal static class FrameSsh
{
    public static async Task<string> ExecuteAsync(FramePreferences preferences, string script, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = Process.Start(CreateSshStartInfo(preferences)) ?? throw new IOException("Could not start the paired Frame connection.");
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
    internal static ProcessStartInfo CreateSshStartInfo(FramePreferences preferences, params string[] remoteCommand)
    {
        if (remoteCommand.Length == 0) remoteCommand = ["python3", "-"];
        var host = preferences.SshHost;
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Any(char.IsWhiteSpace) ||
            !(IPAddress.TryParse(host, out _) || Regex.IsMatch(host, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?\z")))
            throw new ArgumentException("A paired Frame SSH host is required.");
        if (!Path.IsPathFullyQualified(preferences.SshKey) || !File.Exists(preferences.SshKey) ||
            !Path.IsPathFullyQualified(preferences.SshKnownHosts) || !File.Exists(preferences.SshKnownHosts))
            throw new ArgumentException("A paired key and existing trusted host file are required.");
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-F", "NUL", "-T", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes",
            "-o", "StrictHostKeyChecking=yes", "-o", "UserKnownHostsFile=\"" + preferences.SshKnownHosts.Replace('\\', '/') + "\"",
            "-o", "GlobalKnownHostsFile=NUL", "-o", "ConnectTimeout=4", "-o", "ConnectionAttempts=1",
            "-o", "ServerAliveInterval=2", "-o", "ServerAliveCountMax=2", "-i", preferences.SshKey,
            "steamos@" + host }.Concat(remoteCommand)) info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer, token)) != 0)
        { if (text.Length + count > 16384) throw new IOException("Headset response exceeded the metadata limit."); text.Append(buffer, 0, count); }
        return text.ToString();
    }
}
