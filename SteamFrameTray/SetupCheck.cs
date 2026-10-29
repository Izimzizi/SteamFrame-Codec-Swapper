using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SteamFrame.Core;

namespace SteamFrameTray;

public enum StepState { Ok, NeedsAction, Failed, Skipped }

public sealed record SetupStep(string Id, string Title, StepState State, string Detail)
{
    public bool IsOk => State == StepState.Ok;
}

/// <summary>One key the Frame's SSH server presented, in the form pinned to the known-hosts file.</summary>
public sealed record HostKeyLine(string Host, string KeyType, string Base64, string Fingerprint)
{
    public string KnownHostsLine => $"{Host} {KeyType} {Base64}";
}

public sealed record SetupReport(IReadOnlyList<SetupStep> Steps, IReadOnlyList<HostKeyLine> PendingHostKeys)
{
    /// <summary>The bitrate patch is optional, so it never blocks setup.</summary>
    public bool IsComplete => Steps.Where(s => s.Id != SetupCheck.PatchStep).All(s => s.IsOk);
    public SetupStep? Blocking => Steps.FirstOrDefault(s => s.Id != SetupCheck.PatchStep && !s.IsOk);
}

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? input, TimeSpan timeout, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null);
}

/// <summary>Everything about this PC the check looks at, so tests can point it at temporary folders.</summary>
public sealed record SetupEnvironment(string OpenSshDirectory, SteamPaths Steam, string CodecLabDirectory,
    Func<IEnumerable<string>> KeyCandidates, string KnownHostsPath, Func<string, int, CancellationToken, Task<bool>> CanConnect, string? TrayKeyPath = null)
{
    public static SetupEnvironment ForThisPc(SteamPaths steam, string codecLabDirectory) => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH"), steam, codecLabDirectory,
        FramePreferences.DevkitKeyCandidates,
        TrayData.KnownHosts,
        TcpProbe.CanConnectAsync,
        TrayData.TrayKey);
}

public static class TcpProbe
{
    public static async Task<bool> CanConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or IOException) { return false; }
    }
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? input, TimeSpan timeout, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null) foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The program did not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input);
        }
        catch (IOException) { /* the program ended before reading its input */ }
        finally { try { process.StandardInput.Close(); } catch (IOException) { } }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            return new(-1, "", "Timed out.");
        }
        return new(process.ExitCode, await output, await error);
    }
}

/// <summary>
/// The one-time connection setup, as a list of checks with plain-language results. Nothing here changes the
/// Frame: it reads, pins the Frame's public host key once the user confirms it, and tests the logins.
/// </summary>
public sealed partial class SetupCheck
{
    public const string PatchStep = "patch";
    private static readonly string[] HostKeyTypes = ["ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "ssh-rsa"];

    private readonly IProcessRunner _runner;
    private readonly FramePreferences _preferences;
    private readonly Func<Task> _save;
    private readonly ICredentialVault _vault;
    private readonly IDriverPatch? _patch;
    private readonly SetupEnvironment _environment;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public SetupCheck(IProcessRunner runner, FramePreferences preferences, Func<Task> save, ICredentialVault vault, SetupEnvironment environment, IDriverPatch? patch = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _delay = delay ?? Task.Delay;
        _runner = runner; _preferences = preferences; _save = save; _vault = vault; _environment = environment; _patch = patch;
    }

    public string Host => FrameAddress.IsValid(_preferences.SshHost) ? _preferences.SshHost : FrameAddress.DefaultHost;
    private string Ssh => Path.Combine(_environment.OpenSshDirectory, "ssh.exe");
    private string KeyScan => Path.Combine(_environment.OpenSshDirectory, "ssh-keyscan.exe");

    /// <summary>The check that needs no network: everything the supervisor needs is saved and the password is stored.</summary>
    public bool IsConfigured()
    {
        try
        {
            return FrameAddress.IsValid(_preferences.SshHost) && File.Exists(_preferences.SshKey) && File.Exists(_preferences.SshKnownHosts) &&
                   KnownHostEntries(_preferences.SshKnownHosts, _preferences.SshHost).Count > 0 && !string.IsNullOrEmpty(_vault.Load());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return false; }
    }

    public async Task<SetupReport> RunAsync(string? typedPassword = null, CancellationToken cancellationToken = default)
    {
        var steps = new List<SetupStep>();
        var pending = new List<HostKeyLine>();
        var changed = false;

        var openSsh = new[] { "ssh.exe", "ssh-keyscan.exe" }.All(name => File.Exists(Path.Combine(_environment.OpenSshDirectory, name)));
        steps.Add(openSsh ? Ok("openssh", "Windows OpenSSH client", "Found.")
            : Fail("openssh", "Windows OpenSSH client", "Not installed. Open Windows Settings → System → Optional features → Add a feature, install “OpenSSH Client”, then check again."));

        var steamOk = File.Exists(Path.Combine(_environment.Steam.SteamRoot, "steam.exe")) && File.Exists(Path.Combine(_environment.Steam.SteamVrRoot, "bin", "win64", "vrserver.exe"));
        steps.Add(steamOk ? Ok("steam", "Steam and SteamVR", "Found.")
            : Fail("steam", "Steam and SteamVR", "Steam or SteamVR was not found. Install both (SteamVR from the Steam library) and run SteamVR once."));

        var runtimeOk = File.Exists(Path.Combine(_environment.CodecLabDirectory, "runtime", "python.exe")) && File.Exists(Path.Combine(_environment.CodecLabDirectory, "codec_supervisor.py"));
        steps.Add(runtimeOk ? Ok("runtime", "Codec runtime", "Found.")
            : Fail("runtime", "Codec runtime", "The CodecLab folder next to this app is missing or incomplete. Extract the whole download, not just the exe."));

        // The address: the typed or saved one, then the dongle's, whichever answers on the SSH port.
        var host = Host;
        var reachable = await _environment.CanConnect(host, 22, cancellationToken);
        if (!reachable && host != FrameAddress.DongleHost && await _environment.CanConnect(FrameAddress.DongleHost, 22, cancellationToken))
        {
            host = FrameAddress.DongleHost; reachable = true;
        }
        if (reachable)
        {
            changed |= _preferences.SshHost != host;
            _preferences.SshHost = host;
            steps.Add(Ok("address", "Frame reachable", $"{host} answers on the SSH port."));
        }
        else steps.Add(Fail("address", "Frame reachable", $"Nothing answers at “{host}”. Wake the Frame, turn on Developer Mode in its settings, and make sure the PC and Frame are on the same network (or use the dongle). If the name “frame” does not resolve, enter the Frame's IP address."));

        var key = ResolveKey(ref changed);
        var trayKey = false;
        if (key is null && openSsh && _environment.TrayKeyPath is { } created && await GenerateKeyAsync(created, cancellationToken)) { key = created; changed = true; _preferences.SshKey = created; }
        if (key is not null) trayKey = IsTrayKey(key);
        steps.Add(key is not null ? Ok("key", "SSH key", trayKey ? key + " (created by this app)" : key)
            : openSsh ? Fail("key", "SSH key", "A new key could not be created. Check that Windows OpenSSH (ssh-keygen) works, or choose an existing key file.")
            : Skipped("key", "SSH key"));

        // Pin the host key.
        var knownHosts = string.IsNullOrWhiteSpace(_preferences.SshKnownHosts) ? _environment.KnownHostsPath : _preferences.SshKnownHosts;
        if (_preferences.SshKnownHosts != knownHosts) { _preferences.SshKnownHosts = knownHosts; changed = true; }
        if (!openSsh || !reachable) steps.Add(Skipped("hostkey", "Frame host key"));
        else
        {
            var pinned = File.Exists(knownHosts) ? KnownHostEntries(knownHosts, host) : [];
            if (pinned.Count > 0) steps.Add(Ok("hostkey", "Frame host key", $"Pinned ({pinned.Count} key{(pinned.Count == 1 ? "" : "s")} for {host})."));
            else
            {
                var scan = await _runner.RunAsync(KeyScan, ["-T", "5", "-t", "ed25519,ecdsa,rsa", host], null, TimeSpan.FromSeconds(15), cancellationToken);
                var keys = scan.ExitCode == 0 ? ParseKeyScan(host, scan.Output) : [];
                // Windows' ssh-keyscan cannot negotiate with some Frames; ssh itself can, so ask it for the host key.
                if (keys.Count == 0) keys = await ScanWithSshAsync(host, cancellationToken);
                if (keys.Count == 0) steps.Add(Fail("hostkey", "Frame host key", "The Frame did not present an SSH host key. Check that Developer Mode is on and that the Frame is awake, then check again."));
                else
                {
                    pending.AddRange(keys);
                    steps.Add(new("hostkey", "Frame host key", StepState.NeedsAction, "The Frame's host key is not pinned yet. Review its fingerprint and trust it once; it is checked on every connection from now on."));
                }
            }
        }
        var hostKeyReady = steps[^1].IsOk;

        // Login with the key. A key the Frame has not seen is authorized once with the Frame password.
        string? password = typedPassword;
        if (string.IsNullOrEmpty(password))
        {
            try { password = _vault.Load(); } catch (System.ComponentModel.Win32Exception) { password = null; }
        }
        if (!openSsh || !reachable || key is null || !hostKeyReady) steps.Add(Skipped("login", "Login to the Frame"));
        else
        {
            var login = await _runner.RunAsync(Ssh, SshArguments(host, key, knownHosts, "true"), null, TimeSpan.FromSeconds(20), cancellationToken);
            if (login.ExitCode != 0 && login.Error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(password))
                    steps.Add(new("login", "Login to the Frame", StepState.NeedsAction, "The Frame does not know this key yet. Enter the Frame user password once (Frame settings → Developer → Set User Password) and this app will authorize its key. Nothing else on the Frame is changed."));
                else if (!ValidPassword(password))
                    steps.Add(Fail("login", "Login to the Frame", "That password cannot be used here (too long, or it contains a line break)."));
                else
                {
                    var install = await InstallKeyAsync(host, key, knownHosts, password, cancellationToken);
                    if (install is not null) steps.Add(install);
                    else
                    {
                        // The Frame can take a moment to honour a newly added key.
                        for (var attempt = 0; attempt < 3; attempt++)
                        {
                            if (attempt > 0) await _delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
                            login = await _runner.RunAsync(Ssh, SshArguments(host, key, knownHosts, "true"), null, TimeSpan.FromSeconds(20), cancellationToken);
                            if (login.ExitCode == 0) break;
                        }
                        steps.Add(login.ExitCode == 0 ? Ok("login", "Login to the Frame", "The key was authorized on the Frame.") : ClassifyLoginFailure(login));
                    }
                }
            }
            else steps.Add(login.ExitCode == 0 ? Ok("login", "Login to the Frame", "The key is accepted.") : ClassifyLoginFailure(login));
        }

        // Frame password (needed once to get administrator rights for the temporary client swap).
        if (!steps[^1].IsOk) steps.Add(Skipped("password", "Frame password"));
        else
        {
            if (string.IsNullOrEmpty(password))
                steps.Add(new("password", "Frame password", StepState.NeedsAction, "Enter the Frame user password (Frame settings → Developer → Set User Password). It is stored in Windows Credential Manager only after the Frame accepts it."));
            else if (!ValidPassword(password))
                steps.Add(Fail("password", "Frame password", "That password cannot be used here (too long, or it contains a line break)."));
            else
            {
                var sudo = await _runner.RunAsync(Ssh, SshArguments(host, key, knownHosts, "sudo -k -S -p '' true"), password + "\n", TimeSpan.FromSeconds(20), cancellationToken);
                if (sudo.ExitCode == 0)
                {
                    if (!string.IsNullOrEmpty(typedPassword)) _vault.Save(typedPassword);
                    steps.Add(Ok("password", "Frame password", "Accepted."));
                }
                else steps.Add(Fail("password", "Frame password", Regex.IsMatch(sudo.Error, "try again|incorrect password|no password", RegexOptions.IgnoreCase)
                    ? "The Frame rejected that password. Check it under Frame settings → Developer → Set User Password."
                    : "The Frame did not accept administrator rights: " + FirstLine(sudo.Error)));
                password = null;
            }
        }

        steps.Add(await PatchStepAsync());
        try { await _save(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return new(steps, pending);
    }

    /// <summary>Writes the confirmed host key(s) to the known-hosts file. A changed key for an already pinned host is never replaced.</summary>
    public async Task PinHostKeysAsync(IReadOnlyList<HostKeyLine> keys)
    {
        if (keys.Count == 0) throw new ArgumentException("There is no host key to pin.", nameof(keys));
        var path = string.IsNullOrWhiteSpace(_preferences.SshKnownHosts) ? _environment.KnownHostsPath : _preferences.SshKnownHosts;
        var host = keys[0].Host;
        if (!FrameAddress.IsValid(host) || keys.Any(k => k.Host != host)) throw new ArgumentException("The host key does not belong to the Frame's address.", nameof(keys));
        var existing = File.Exists(path) ? KnownHostEntries(path, host) : [];
        if (existing.Any(e => keys.Any(k => k.KeyType == e.KeyType && k.Base64 != e.Base64)))
            throw new InvalidOperationException("The Frame's host key does not match the one pinned earlier. Do not continue unless you reset the Frame or know why it changed; delete its line from the known-hosts file to pin it again.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var additions = keys.Where(k => !existing.Any(e => e.KeyType == k.KeyType && e.Base64 == k.Base64)).Select(k => k.KnownHostsLine).ToList();
        if (additions.Count > 0)
        {
            var prefix = File.Exists(path) && new FileInfo(path).Length > 0 && !File.ReadAllText(path).EndsWith('\n') ? "\n" : "";
            await File.AppendAllTextAsync(path, prefix + string.Join("\n", additions) + "\n");
        }
        _preferences.SshKnownHosts = path;
        await _save();
    }

    public static string FingerprintOf(byte[] keyBlob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(keyBlob)).TrimEnd('=');

    /// <summary>Reads ssh-keyscan output, keeping only well-formed keys of known types for exactly this host.</summary>
    public static IReadOnlyList<HostKeyLine> ParseKeyScan(string host, string output)
    {
        var keys = new Dictionary<string, HostKeyLine>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !fields[0].Split(',').Contains(host) || !HostKeyTypes.Contains(fields[1])) continue;
            byte[] blob;
            try { blob = Convert.FromBase64String(fields[2]); } catch (FormatException) { continue; }
            if (!BlobNames(blob, fields[1])) continue;
            keys.TryAdd(fields[1], new(host, fields[1], fields[2], FingerprintOf(blob)));
        }
        return keys.Values.ToList();
    }

    /// <summary>The key blob starts with its own length-prefixed type name, which must match the declared type.</summary>
    private static bool BlobNames(byte[] blob, string type)
    {
        var name = Encoding.ASCII.GetBytes(type);
        if (blob.Length < 4 + name.Length) return false;
        var length = (blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3];
        return length == name.Length && blob.AsSpan(4, name.Length).SequenceEqual(name);
    }

    private static List<(string KeyType, string Base64)> KnownHostEntries(string path, string host)
    {
        var entries = new List<(string, string)>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == '|' || line[0] == '@') continue; // hashed and marker entries cannot be matched here
            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3) continue;
            if (fields[0].Split(',').Any(h => h == host || h == $"[{host}]:22")) entries.Add((fields[1], fields[2]));
        }
        return entries;
    }

    private static List<string> SshArguments(string host, string key, string knownHosts, string command) =>
        ["-i", key, "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "UserKnownHostsFile=" + knownHosts,
         "-o", "ConnectTimeout=5", "steamos@" + host, command];

    /// <summary>Connects with ssh to a throwaway known-hosts file and returns the host key the Frame presented. Nothing is trusted here; the user confirms it later.</summary>
    private async Task<IReadOnlyList<HostKeyLine>> ScanWithSshAsync(string host, CancellationToken cancellationToken)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "steamframe-hostkey-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] arguments = ["-o", "StrictHostKeyChecking=accept-new", "-o", "UserKnownHostsFile=" + scratch, "-o", "HashKnownHosts=no", "-o", "CheckHostIP=no",
                "-o", "BatchMode=yes", "-o", "PreferredAuthentications=none", "-o", "PubkeyAuthentication=no", "-o", "ConnectTimeout=5", "steamos@" + host, "true"];
            await _runner.RunAsync(Ssh, arguments, null, TimeSpan.FromSeconds(20), cancellationToken);
            return File.Exists(scratch) ? ParseKeyScan(host, File.ReadAllText(scratch)) : [];
        }
        finally { try { File.Delete(scratch); } catch (IOException) { } }
    }

    private static bool ValidPassword(string password) => password.Length <= 128 && !password.Contains('\r') && !password.Contains('\n');

    private bool IsTrayKey(string key) => _environment.TrayKeyPath is { } own && Path.GetFullPath(own).Equals(Path.GetFullPath(key), StringComparison.OrdinalIgnoreCase);

    /// <summary>The saved key if it exists, else this app's own key, else a devkit key found on this PC.</summary>
    private string? ResolveKey(ref bool changed)
    {
        var key = _preferences.SshKey;
        if (!string.IsNullOrWhiteSpace(key) && File.Exists(key)) return key;
        var found = (_environment.TrayKeyPath is { } own && File.Exists(own) ? own : null) ?? _environment.KeyCandidates().FirstOrDefault(File.Exists);
        if (found is null) return null;
        _preferences.SshKey = found; changed = true;
        return found;
    }

    private async Task<bool> GenerateKeyAsync(string path, CancellationToken cancellationToken)
    {
        var keygen = Path.Combine(_environment.OpenSshDirectory, "ssh-keygen.exe");
        if (!File.Exists(keygen) || File.Exists(path)) return false; // an existing key is never overwritten
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var result = await _runner.RunAsync(keygen, ["-q", "-t", "ed25519", "-N", "", "-C", "steamframe-tray", "-f", path], null, TimeSpan.FromSeconds(20), cancellationToken);
        return result.ExitCode == 0 && File.Exists(path);
    }

    /// <summary>Adds the key's public half to the Frame's authorized_keys using the Frame password. Returns a failed step, or null on success.</summary>
    private async Task<SetupStep?> InstallKeyAsync(string host, string key, string knownHosts, string password, CancellationToken cancellationToken)
    {
        var keygen = Path.Combine(_environment.OpenSshDirectory, "ssh-keygen.exe");
        var derived = await _runner.RunAsync(keygen, ["-y", "-f", key], null, TimeSpan.FromSeconds(15), cancellationToken);
        var match = PublicKeyPattern().Match(derived.Output.Trim());
        if (derived.ExitCode != 0 || !match.Success)
            return Fail("login", "Login to the Frame", "The public half of this key could not be read (is the key protected by a passphrase?). Choose a key without a passphrase.");
        var line = $"{match.Groups[1].Value} {match.Groups[2].Value} steamframe-tray";
        var command = $"umask 077; mkdir -p ~/.ssh; touch ~/.ssh/authorized_keys; grep -qxF '{line}' ~/.ssh/authorized_keys || echo '{line}' >> ~/.ssh/authorized_keys";
        string[] arguments = ["-o", "PreferredAuthentications=password", "-o", "PubkeyAuthentication=no", "-o", "NumberOfPasswordPrompts=1",
            "-o", "StrictHostKeyChecking=yes", "-o", "UserKnownHostsFile=" + knownHosts, "-o", "ConnectTimeout=5", "steamos@" + host, command];
        // The password reaches ssh only through its askpass helper (this program) and an environment variable of that one process.
        var environment = new Dictionary<string, string>
        {
            ["SSH_ASKPASS"] = Environment.ProcessPath ?? "", ["SSH_ASKPASS_REQUIRE"] = "force", ["DISPLAY"] = "steamframe", [AskPass.Variable] = password,
        };
        var result = await _runner.RunAsync(Ssh, arguments, null, TimeSpan.FromSeconds(30), cancellationToken, environment);
        if (result.ExitCode == 0) return null;
        return Fail("login", "Login to the Frame", result.Error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            ? "The Frame rejected that password. Check it under Frame settings → Developer → Set User Password."
            : "The key could not be authorized: " + FirstLine(result.Error));
    }

    [GeneratedRegex(@"^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(?:256|384|521)) ([A-Za-z0-9+/]+={0,2})(?: [^\r\n]*)?$")]
    private static partial Regex PublicKeyPattern();

    private static SetupStep ClassifyLoginFailure(ProcessResult result)
    {
        var error = result.Error;
        if (error.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase) || error.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
            return Fail("login", "Login to the Frame", "The Frame's host key no longer matches the pinned one. Do not continue unless you reset the Frame; remove its line from the known-hosts file to pin the new key.");
        if (error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return new("login", "Login to the Frame", StepState.NeedsAction, "The Frame still refuses this key. Check again in a moment; if it persists, restart the Frame once and check again. If you chose a key file yourself, it must be one the Frame already accepts.");
        if (error.Contains("timed out", StringComparison.OrdinalIgnoreCase) || error.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase) || error.Contains("refused", StringComparison.OrdinalIgnoreCase) || error.Contains("No route", StringComparison.OrdinalIgnoreCase))
            return Fail("login", "Login to the Frame", "The Frame could not be reached over SSH. Wake it and make sure Developer Mode is on.");
        return Fail("login", "Login to the Frame", "Login failed: " + FirstLine(error));
    }

    private async Task<SetupStep> PatchStepAsync()
    {
        if (_patch is null) return Ok(PatchStep, "Bitrate patch (optional)", "Not checked.");
        try
        {
            var status = await _patch.InspectAsync();
            return Ok(PatchStep, "Bitrate patch (optional)", status.IsPatched ? "Installed: bitrates up to 500 Mbps are available."
                : status.IsSupported ? "Not installed: SteamVR limits the bitrate to 232 Mbps. Install it from the tray menu (SteamVR must be closed)."
                : "SteamVR's driver is not recognised, so the bitrate patch is unavailable.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { return Ok(PatchStep, "Bitrate patch (optional)", "Could not check: " + error.Message); }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no details";
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    private static SetupStep Ok(string id, string title, string detail) => new(id, title, StepState.Ok, detail);
    private static SetupStep Fail(string id, string title, string detail) => new(id, title, StepState.Failed, detail);
    private static SetupStep Skipped(string id, string title) => new(id, title, StepState.Skipped, "Waiting for the step above.");
}

/// <summary>
/// ssh runs this program (SSH_ASKPASS) to ask for the Frame password. The password lives in an environment variable of that one
/// ssh process only; it is never written to a file or a command line.
/// </summary>
public static class AskPass
{
    public const string Variable = "STEAMFRAME_ASKPASS_PW";

    /// <summary>Answers a password prompt and returns true when this process was started as ssh's askpass helper.</summary>
    public static bool TryAnswer(string[] args)
    {
        var password = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(password)) return false;
        var prompt = string.Join(' ', args);
        if (prompt.Contains("assword", StringComparison.OrdinalIgnoreCase) && !prompt.Contains("passphrase", StringComparison.OrdinalIgnoreCase))
        {
            using var output = Console.OpenStandardOutput();
            var bytes = System.Text.Encoding.UTF8.GetBytes(password + "\n");
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
        }
        return true;
    }
}
