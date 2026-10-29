using System.Text;
using SteamFrameTray;
using SteamFrame.Core;

namespace TrayTests;

internal sealed class FakeRunner : IProcessRunner
{
    public Func<string, IReadOnlyList<string>, string?, ProcessResult> Handler { get; set; } = (_, _, _) => new(0, "", "");
    public List<(string File, string[] Args, string? Input)> Calls { get; } = [];
    public List<IReadOnlyDictionary<string, string>?> Environments { get; } = [];
    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? input, TimeSpan timeout, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
    {
        Environments.Add(environment);
        Calls.Add((Path.GetFileName(fileName), arguments.ToArray(), input));
        return Task.FromResult(Handler(Path.GetFileName(fileName), arguments, input));
    }
}

public sealed class SetupCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "setupcheck-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();
    private readonly FakeVault _vault = new();
    private readonly FramePreferences _preferences = new();
    private readonly List<string> _reachable = ["frame"];
    private int _saves;

    private static byte[] Blob(string type)
    {
        var name = Encoding.ASCII.GetBytes(type);
        return [0, 0, 0, (byte)name.Length, .. name, 1, 2, 3, 4];
    }

    private static string KeyScanLine(string host, string type, byte[]? blob = null) => $"{host} {type} {Convert.ToBase64String(blob ?? Blob(type))}";

    private SetupCheck Make()
    {
        Directory.CreateDirectory(Path.Combine(_root, "ssh"));
        File.WriteAllText(Path.Combine(_root, "ssh", "ssh.exe"), "");
        File.WriteAllText(Path.Combine(_root, "ssh", "ssh-keyscan.exe"), "");
        File.WriteAllText(Path.Combine(_root, "ssh", "ssh-keygen.exe"), "");
        Directory.CreateDirectory(Path.Combine(_root, "steam", "steamapps", "common", "SteamVR", "bin", "win64"));
        File.WriteAllText(Path.Combine(_root, "steam", "steam.exe"), "");
        File.WriteAllText(Path.Combine(_root, "steam", "steamapps", "common", "SteamVR", "bin", "win64", "vrserver.exe"), "");
        Directory.CreateDirectory(Path.Combine(_root, "lab", "runtime"));
        File.WriteAllText(Path.Combine(_root, "lab", "runtime", "python.exe"), "");
        File.WriteAllText(Path.Combine(_root, "lab", "codec_supervisor.py"), "");
        var key = Path.Combine(_root, "devkit_rsa");
        File.WriteAllText(key, "key");
        _preferences.SshHost = "frame";
        _preferences.SshKey = key;
        _preferences.SshKnownHosts = Path.Combine(_root, "known_hosts");
        var steam = new SteamPaths(Path.Combine(_root, "steam"), Path.Combine(_root, "steam", "steamapps", "common", "SteamVR"));
        var environment = new SetupEnvironment(Path.Combine(_root, "ssh"), steam, Path.Combine(_root, "lab"), () => [key], Path.Combine(_root, "known_hosts"),
            (host, _, _) => Task.FromResult(_reachable.Contains(host)));
        return new SetupCheck(_runner, _preferences, () => { _saves++; return Task.CompletedTask; }, _vault, environment, new FakePatch(), (_, _) => Task.CompletedTask);
    }

    [Fact]
    public void ParseKeyScanKeepsOnlyWellFormedKeysForTheHost()
    {
        var output = string.Join("\n", "# frame:22 SSH-2.0", KeyScanLine("frame", "ssh-ed25519"), KeyScanLine("frame", "ssh-rsa"),
            KeyScanLine("other", "ssh-ed25519"), "frame ssh-ed25519 !!notbase64!!", KeyScanLine("frame", "ecdsa-sha2-nistp256", Blob("ssh-rsa")),
            KeyScanLine("frame", "ssh-ed25519"), "frame ssh-dss AAAA");
        var keys = SetupCheck.ParseKeyScan("frame", output);
        Assert.Equal(["ssh-ed25519", "ssh-rsa"], keys.Select(k => k.KeyType).ToArray());
        Assert.All(keys, k => Assert.StartsWith("SHA256:", k.Fingerprint));
        Assert.DoesNotContain('=', keys[0].Fingerprint);
    }

    [Fact]
    public async Task UnpinnedHostKeyNeedsConfirmationAndIsOnlyWrittenOnPin()
    {
        var check = Make();
        _runner.Handler = (file, _, _) => file == "ssh-keyscan.exe" ? new(0, KeyScanLine("frame", "ssh-ed25519") + "\n", "") : new(0, "", "");
        var report = await check.RunAsync();
        Assert.Equal(StepState.NeedsAction, report.Steps.Single(s => s.Id == "hostkey").State);
        Assert.Equal(StepState.Skipped, report.Steps.Single(s => s.Id == "login").State);
        Assert.False(File.Exists(Path.Combine(_root, "known_hosts")));
        Assert.Single(report.PendingHostKeys);

        await check.PinHostKeysAsync(report.PendingHostKeys);
        Assert.Equal(report.PendingHostKeys[0].KnownHostsLine + "\n", File.ReadAllText(Path.Combine(_root, "known_hosts")));
        var again = await check.RunAsync();
        Assert.True(again.Steps.Single(s => s.Id == "hostkey").IsOk);
        Assert.Empty(again.PendingHostKeys);
    }

    [Fact]
    public async Task When_ssh_keyscan_gets_nothing_the_host_key_is_read_through_ssh_into_a_throwaway_file()
    {
        var check = Make();
        string? scratch = null;
        _runner.Handler = (file, args, _) =>
        {
            if (file == "ssh-keyscan.exe") return new(1, "", "choose_kex: unsupported KEX method");
            scratch = args.Single(a => a.StartsWith("UserKnownHostsFile=")).Substring("UserKnownHostsFile=".Length);
            File.WriteAllText(scratch, KeyScanLine("frame,10.35.78.1", "ssh-ed25519") + "\n");
            return new(255, "", "Permission denied (none).");
        };
        var report = await check.RunAsync();
        var key = Assert.Single(report.PendingHostKeys);
        Assert.Equal("frame", key.Host); Assert.StartsWith("SHA256:", key.Fingerprint);
        Assert.Equal(StepState.NeedsAction, report.Steps.Single(s => s.Id == "hostkey").State);
        Assert.False(File.Exists(scratch!));
        Assert.False(File.Exists(Path.Combine(_root, "known_hosts")));
    }

    [Fact]
    public async Task ChangedHostKeyIsNeverReplaced()
    {
        var check = Make();
        var original = KeyScanLine("frame", "ssh-ed25519");
        File.WriteAllText(Path.Combine(_root, "known_hosts"), original + "\n");
        var changed = SetupCheck.ParseKeyScan("frame", KeyScanLine("frame", "ssh-ed25519", [0, 0, 0, 11, .. Encoding.ASCII.GetBytes("ssh-ed25519"), 9, 9]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => check.PinHostKeysAsync(changed));
        Assert.Equal(original + "\n", File.ReadAllText(Path.Combine(_root, "known_hosts")));
    }

    [Fact]
    public async Task FullSuccessStoresThePasswordOnlyAfterTheFrameAcceptsIt()
    {
        var check = Make();
        File.WriteAllText(Path.Combine(_root, "known_hosts"), KeyScanLine("frame", "ssh-ed25519") + "\n");
        _runner.Handler = (_, _, _) => new(0, "", "");
        var report = await check.RunAsync("secret");
        Assert.True(report.IsComplete);
        Assert.Equal("secret", _vault.Stored);
        var sudo = _runner.Calls.Single(c => c.Input is not null);
        Assert.Equal("secret\n", sudo.Input);
        Assert.DoesNotContain(sudo.Args, a => a.Contains("secret"));
        Assert.Contains("StrictHostKeyChecking=yes", sudo.Args);
        Assert.Contains("steamos@frame", sudo.Args);
        Assert.True(_saves > 0);
        Assert.True(check.IsConfigured());
    }

    [Fact]
    public async Task RejectedPasswordIsNotStored()
    {
        var check = Make();
        File.WriteAllText(Path.Combine(_root, "known_hosts"), KeyScanLine("frame", "ssh-ed25519") + "\n");
        _runner.Handler = (_, _, input) => input is null ? new(0, "", "") : new(1, "", "Sorry, try again.");
        var report = await check.RunAsync("wrong");
        Assert.Equal(StepState.Failed, report.Steps.Single(s => s.Id == "password").State);
        Assert.Null(_vault.Stored);
        Assert.False(report.IsComplete);
    }

    [Theory]
    [InlineData("Permission denied (publickey).", StepState.NeedsAction)]
    [InlineData("WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!", StepState.Failed)]
    [InlineData("ssh: connect to host frame port 22: Connection timed out", StepState.Failed)]
    public async Task LoginFailuresAreClassified(string error, StepState expected)
    {
        var check = Make();
        File.WriteAllText(Path.Combine(_root, "known_hosts"), KeyScanLine("frame", "ssh-ed25519") + "\n");
        _runner.Handler = (_, _, _) => new(255, "", error);
        var report = await check.RunAsync();
        var login = report.Steps.Single(s => s.Id == "login");
        Assert.Equal(expected, login.State);
        Assert.Equal(StepState.Skipped, report.Steps.Single(s => s.Id == "password").State);
    }

    [Fact]
    public async Task FallsBackToTheDongleAddressWhenOnlyThatAnswers()
    {
        var check = Make();
        _reachable.Clear(); _reachable.Add(FrameAddress.DongleHost);
        var report = await check.RunAsync();
        Assert.True(report.Steps.Single(s => s.Id == "address").IsOk);
        Assert.Equal(FrameAddress.DongleHost, _preferences.SshHost);
    }

    [Fact]
    public async Task UnreachableFrameSkipsTheNetworkSteps()
    {
        var check = Make();
        _reachable.Clear();
        var report = await check.RunAsync();
        Assert.Equal(StepState.Failed, report.Steps.Single(s => s.Id == "address").State);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task MissingKeyIsFoundInCandidateLocations()
    {
        var check = Make();
        var found = Path.Combine(_root, "found_key");
        File.WriteAllText(found, "k");
        _preferences.SshKey = Path.Combine(_root, "missing");
        var environment = new SetupEnvironment(Path.Combine(_root, "ssh"), new SteamPaths(Path.Combine(_root, "steam"), Path.Combine(_root, "steam", "steamapps", "common", "SteamVR")),
            Path.Combine(_root, "lab"), () => [Path.Combine(_root, "nope"), found], Path.Combine(_root, "known_hosts"), (h, _, _) => Task.FromResult(h == "frame"));
        var report = await new SetupCheck(_runner, _preferences, () => Task.CompletedTask, _vault, environment).RunAsync();
        Assert.Equal(found, _preferences.SshKey);
        Assert.True(report.Steps.Single(s => s.Id == "key").IsOk);
    }

    [Fact]
    public async Task NotConfiguredWithoutAStoredPassword()
    {
        var check = Make();
        File.WriteAllText(Path.Combine(_root, "known_hosts"), KeyScanLine("frame", "ssh-ed25519") + "\n");
        Assert.False(check.IsConfigured());
        _vault.Stored = "pw";
        Assert.True(check.IsConfigured());
        await Task.CompletedTask;
    }

    private const string PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGJ1c3RlZGtleQ steamframe-tray";

    private SetupCheck MakeWithTrayKey(out string trayKey)
    {
        var check = Make();
        trayKey = Path.Combine(_root, "own", "frame_ed25519");
        File.Delete(Path.Combine(_root, "devkit_rsa"));
        _preferences.SshKey = Path.Combine(_root, "devkit_rsa");
        var env = new SetupEnvironment(Path.Combine(_root, "ssh"), new SteamPaths(Path.Combine(_root, "steam"), Path.Combine(_root, "steam", "steamapps", "common", "SteamVR")),
            Path.Combine(_root, "lab"), () => [], Path.Combine(_root, "known_hosts"), (h, _, _) => Task.FromResult(_reachable.Contains(h)), trayKey);
        File.WriteAllText(Path.Combine(_root, "known_hosts"), KeyScanLine("frame", "ssh-ed25519") + "\n");
        return new SetupCheck(_runner, _preferences, () => { _saves++; return Task.CompletedTask; }, _vault, env, null, (_, _) => Task.CompletedTask);
    }

    [Fact]
    public async Task Without_any_key_the_app_creates_its_own_and_never_overwrites_one()
    {
        var check = MakeWithTrayKey(out var trayKey);
        _runner.Handler = (file, args, _) =>
        {
            if (file == "ssh-keygen.exe" && args.Contains("-t")) { Directory.CreateDirectory(Path.GetDirectoryName(trayKey)!); File.WriteAllText(trayKey, "k"); }
            return new(0, "", "");
        };
        var report = await check.RunAsync();
        Assert.Equal(trayKey, _preferences.SshKey);
        Assert.True(report.Steps.Single(s => s.Id == "key").IsOk);
        var generate = _runner.Calls.Single(c => c.File == "ssh-keygen.exe");
        Assert.Contains("ed25519", generate.Args); Assert.Contains("-N", generate.Args);

        _runner.Calls.Clear();
        await check.RunAsync();
        Assert.DoesNotContain(_runner.Calls, c => c.File == "ssh-keygen.exe" && c.Args.Contains("-t"));
    }

    [Fact]
    public async Task An_unknown_key_is_authorized_with_the_password_which_never_appears_in_arguments()
    {
        var check = MakeWithTrayKey(out var trayKey);
        Directory.CreateDirectory(Path.GetDirectoryName(trayKey)!); File.WriteAllText(trayKey, "k");
        var authorized = false;
        _runner.Handler = (file, args, input) =>
        {
            if (file == "ssh-keygen.exe") return new(0, PublicKey + "\n", "");
            if (args.Contains("PreferredAuthentications=password")) { authorized = true; return new(0, "", ""); }
            return authorized || input is not null ? new(0, "", "") : new(255, "", "steamos@frame: Permission denied (publickey,password).");
        };
        var report = await check.RunAsync("pw-secret");
        Assert.True(report.IsComplete);
        var install = _runner.Calls.Single(c => c.Args.Contains("PreferredAuthentications=password"));
        Assert.DoesNotContain(install.Args, a => a.Contains("pw-secret"));
        Assert.Contains("StrictHostKeyChecking=yes", install.Args);
        Assert.Contains(install.Args, a => a.Contains("grep -qxF 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGJ1c3RlZGtleQ steamframe-tray'"));
        var environment = _runner.Environments[_runner.Calls.IndexOf(install)]!;
        Assert.Equal("pw-secret", environment[AskPass.Variable]); Assert.Equal("force", environment["SSH_ASKPASS_REQUIRE"]);
        Assert.All(_runner.Environments.Where((e, i) => i != _runner.Calls.IndexOf(install)), e => Assert.Null(e));
        Assert.Equal("pw-secret", _vault.Stored);
    }

    [Fact]
    public async Task An_unknown_key_without_a_password_asks_for_it()
    {
        var check = MakeWithTrayKey(out var trayKey);
        Directory.CreateDirectory(Path.GetDirectoryName(trayKey)!); File.WriteAllText(trayKey, "k");
        _runner.Handler = (_, _, _) => new(255, "", "Permission denied (publickey,password).");
        var report = await check.RunAsync();
        Assert.Equal(StepState.NeedsAction, report.Steps.Single(s => s.Id == "login").State);
        Assert.DoesNotContain(_runner.Calls, c => c.Args.Contains("PreferredAuthentications=password"));
    }

    [Fact]
    public async Task A_rejected_password_authorizes_nothing_and_stores_nothing()
    {
        var check = MakeWithTrayKey(out var trayKey);
        Directory.CreateDirectory(Path.GetDirectoryName(trayKey)!); File.WriteAllText(trayKey, "k");
        _runner.Handler = (file, _, _) => file == "ssh-keygen.exe" ? new(0, PublicKey, "") : new(255, "", "Permission denied, please try again.");
        var report = await check.RunAsync("wrong");
        Assert.Equal(StepState.Failed, report.Steps.Single(s => s.Id == "login").State);
        Assert.Null(_vault.Stored);
    }

    [Fact]
    public async Task A_public_key_that_does_not_look_right_is_never_sent()
    {
        var check = MakeWithTrayKey(out var trayKey);
        Directory.CreateDirectory(Path.GetDirectoryName(trayKey)!); File.WriteAllText(trayKey, "k");
        _runner.Handler = (file, _, _) => file == "ssh-keygen.exe" ? new(0, "ssh-ed25519 AAAA'; rm -rf ~ #", "") : new(255, "", "Permission denied (publickey,password).");
        var report = await check.RunAsync("pw");
        Assert.Equal(StepState.Failed, report.Steps.Single(s => s.Id == "login").State);
        Assert.DoesNotContain(_runner.Calls, c => c.Args.Contains("PreferredAuthentications=password"));
    }

    [Fact]
    public void The_askpass_helper_answers_only_password_prompts_when_asked_to()
    {
        Environment.SetEnvironmentVariable(AskPass.Variable, null);
        Assert.False(AskPass.TryAnswer(["steamos@frame's password: "]));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
}
