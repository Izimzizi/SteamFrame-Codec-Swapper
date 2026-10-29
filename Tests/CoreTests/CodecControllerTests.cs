using System.Buffers.Binary;
using System.Text.Json;
using SteamLinkVRSettings.Frame;

public sealed class CodecControllerTests
{
    [Theory]
    [InlineData(StreamCodec.H264, 500, true)]
    [InlineData(StreamCodec.H264, 501, false)]
    [InlineData(StreamCodec.Av1, 200, true)]
    [InlineData(StreamCodec.Av1, 201, false)]
    [InlineData(StreamCodec.Hevc, 500, true)]
    [InlineData(StreamCodec.H264, 24, false)]
    public void Codec_specific_budgets_preserve_AV1_bounds(StreamCodec codec, int target, bool accepted)
    {
        var request = new CodecSwitchRequest(codec, target, 90);
        if (accepted) request.Validate();
        else Assert.Throws<ArgumentOutOfRangeException>(() => request.Validate());
    }
    [Fact]
    public async Task Older_companion_rejects_high_H264_before_publishing_request()
    {
        using var lab = new CodecTestDirectory(); lab.Status(codec: "H.264", maximumH264: null);
        var controller = new CodecController(lab.Root, lab.Root, _ => 100, () => 1000);
        Assert.Equal(200, (await controller.ReadStatusAsync()).MaximumH264Mbps);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SwitchAsync(new(StreamCodec.H264, 300, 90), new(), null));
        Assert.Contains("updated runtime", error.Message);
        Assert.False(Directory.Exists(Path.Combine(lab.Root, "requests")));
    }
    [Fact]
    public async Task Status_requires_fresh_heartbeat_and_matching_owner_creation()
    {
        using var lab = new CodecTestDirectory();
        var controller = new CodecController(lab.Root, lab.Root, _ => 100, () => 1000);
        Assert.True((await controller.ReadStatusAsync()).CanStart);
        lab.Status(updated: 1000);
        Assert.True((await controller.ReadStatusAsync()).Live);
        lab.Status(updated: 993);
        var stale = await controller.ReadStatusAsync();
        Assert.False(stale.Live); Assert.False(stale.CanStart);
        lab.Status(created: 103, updated: 1000);
        Assert.False((await controller.ReadStatusAsync()).Live);
        Assert.False((await controller.ReadStatusAsync()).CanStart);
        lab.Status(updated: 1003);
        Assert.False((await controller.ReadStatusAsync()).Live);
        var dead = new CodecController(lab.Root, lab.Root, _ => null, () => 1000);
        Assert.True((await dead.ReadStatusAsync()).CanStart);
        File.WriteAllText(Path.Combine(lab.Root, "status.json"), "{invalid}");
        // An unreadable status blocks a new session only while a supervisor holds the owner lock.
        using (var owner = new FileStream(Path.Combine(lab.Root, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            owner.WriteByte((byte)'0'); owner.Flush(); owner.Lock(0, 1);
            Assert.False((await controller.ReadStatusAsync()).CanStart);
        }
        Assert.True((await controller.ReadStatusAsync()).CanStart);
    }

    [Fact]
    public async Task Closed_session_is_not_live_while_its_owner_exits()
    {
        using var lab = new CodecTestDirectory(); lab.Status(state: "closed", codec: "HEVC", ok: true);
        var controller = new CodecController(lab.Root, lab.Root, _ => 100, () => 1000);
        var status = await controller.ReadStatusAsync();
        Assert.False(status.Live); Assert.False(status.CanStart); Assert.Equal("closed", status.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.CloseAsync());
        Assert.False(Directory.Exists(Path.Combine(lab.Root, "requests")));
    }

    [Fact]
    public async Task Existing_session_switches_without_authentication_and_matches_its_operation_id()
    {
        using var lab = new CodecTestDirectory(); lab.Status();
        var controller = new CodecController(lab.Root, lab.Root, _ => 100, () => 1000);
        var switchTask = controller.SwitchAsync(new(StreamCodec.H264, 100, 90), new(), "must-never-be-written");
        var path = await lab.WaitForRequest();
        using var request = JsonDocument.Parse(File.ReadAllText(path));
        var json = request.RootElement;
        Assert.Equal("H.264", json.GetProperty("codec").GetString());
        Assert.Equal("switch", json.GetProperty("action").GetString());
        Assert.Equal(100, json.GetProperty("target_mbps").GetInt32());
        Assert.Equal(90, json.GetProperty("fps").GetInt32());
        Assert.False(json.TryGetProperty("password", out _));
        lab.Status(operation: Guid.NewGuid().ToString("D"), codec: "H.264", ok: true);
        await Task.Delay(500); Assert.False(switchTask.IsCompleted); // An unrelated result is not completion.
        lab.Status(operation: json.GetProperty("id").GetString(), codec: "H.264", ok: true);
        var result = await switchTask;
        Assert.Equal("H.264", result.ActiveCodec);
        Assert.DoesNotContain("must-never-be-written", string.Join("", Directory.GetFiles(lab.Root, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_session_with_reused_pid_only_unblocks_without_an_owner_lock(bool locked)
    {
        using var lab = new CodecTestDirectory(); lab.Status(state: "closed", codec: "HEVC", ok: true);
        using var owner = new FileStream(Path.Combine(lab.Root, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        owner.WriteByte((byte)'0'); owner.Flush();
        if (locked) owner.Lock(0, 1);
        var controller = new CodecController(lab.Root, lab.Root, _ => 200, () => 1000);
        var status = await controller.ReadStatusAsync(TestContext.Current.CancellationToken);
        Assert.False(status.Live);
        Assert.Equal(!locked, status.CanStart);
        Assert.False(Directory.Exists(Path.Combine(lab.Root, "requests")));
    }

    [Fact]
    public async Task A_bitrate_only_change_sends_a_small_rate_request_and_only_when_the_session_allows_it()
    {
        using var lab = new CodecTestDirectory(); lab.Status(codec: "H.264");
        var controller = new CodecController(lab.Root, lab.Root, _ => 100, () => 1000);
        Assert.False((await controller.ReadStatusAsync()).RateLive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SetRateAsync(400));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.SetRateAsync(501));
        Assert.False(Directory.Exists(Path.Combine(lab.Root, "requests")));

        lab.Status(codec: "H.264", rateLive: true);
        Assert.True((await controller.ReadStatusAsync()).RateLive);
        var changing = controller.SetRateAsync(400);
        var path = await lab.WaitForRequest();
        using var request = JsonDocument.Parse(File.ReadAllText(path));
        var json = request.RootElement;
        Assert.Equal("rate", json.GetProperty("action").GetString());
        Assert.Equal(400, json.GetProperty("target_mbps").GetInt32());
        Assert.False(json.TryGetProperty("codec", out _)); Assert.False(json.TryGetProperty("password", out _));
        lab.Status(codec: "H.264", rateLive: true, operation: json.GetProperty("id").GetString(), ok: true);
        Assert.Equal("H.264", (await changing).ActiveCodec);
    }

    [Fact]
    public async Task A_refused_rate_change_is_reported_without_ending_the_session()
    {
        using var lab = new CodecTestDirectory(); lab.Status(codec: "H.264", rateLive: true);
        var controller = new CodecController(lab.Root, lab.Root, _ => 100, () => 1000);
        var changing = controller.SetRateAsync(400);
        var path = await lab.WaitForRequest();
        using var request = JsonDocument.Parse(File.ReadAllText(path));
        lab.Status(codec: "H.264", rateLive: true, operation: request.RootElement.GetProperty("id").GetString(), ok: false, error: "Live bitrate change unavailable: not available");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => changing);
        Assert.Contains("unavailable", error.Message);
        Assert.True((await controller.ReadStatusAsync()).Live);
    }

    [Fact]
    public async Task Stale_owner_gets_no_request_and_failed_operation_is_reported()
    {
        using var lab = new CodecTestDirectory(); lab.Status(updated: 990);
        var controller = new CodecController(lab.Root, lab.Root, _ => 100, () => 1000);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SwitchAsync(new(StreamCodec.Av1), new(), null));
        Assert.False(Directory.Exists(Path.Combine(lab.Root, "requests")));
        lab.Status();
        var switching = controller.SwitchAsync(new(StreamCodec.Av1), new(), null);
        var path = await lab.WaitForRequest();
        using var request = JsonDocument.Parse(File.ReadAllText(path));
        lab.Status(state: "error", operation: request.RootElement.GetProperty("id").GetString(), ok: false, error: "Decoder guard rejected this build.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => switching);
        Assert.Contains("Decoder guard", error.Message);
    }

    [Fact]
    public async Task Closing_accepts_verified_rollback_receipt_after_owner_exits()
    {
        using var lab = new CodecTestDirectory(); lab.Status(codec: "AV1");
        bool alive = true;
        var controller = new CodecController(lab.Root, lab.Root, _ => alive ? 100 : null, () => 1000);
        var closing = controller.CloseAsync();
        var path = await lab.WaitForRequest();
        using var request = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("close", request.RootElement.GetProperty("action").GetString());
        lab.Status(state: "closed", codec: "HEVC", operation: request.RootElement.GetProperty("id").GetString(), ok: true);
        alive = false;
        Assert.Equal("closed", (await closing).State);
    }

    [Fact]
    public async Task Startup_configuration_contains_no_password_even_when_runtime_is_missing()
    {
        using var lab = new CodecTestDirectory();
        var key = Path.Combine(lab.Root, "key"); var hosts = Path.Combine(lab.Root, "hosts");
        File.WriteAllText(key, "test"); File.WriteAllText(hosts, "test");
        var paths = new SteamPaths(Path.Combine(lab.Root, "Steam"), Path.Combine(lab.Root, "Other Library", "SteamVR"));
        var controller = new CodecController(lab.Root, Path.Combine(lab.Root, "missing-runtime"), _ => null, () => 1000, paths);
        await Assert.ThrowsAsync<FileNotFoundException>(() => controller.SwitchAsync(new(StreamCodec.Av1),
            new() { SshHost = "10.35.78.1", SshKey = key, SshKnownHosts = hosts }, "private-test-password"));
        var config = File.ReadAllText(Assert.Single(Directory.GetFiles(lab.Root, "config-*.json")));
        Assert.DoesNotContain("password", config);
        using var document = JsonDocument.Parse(config);
        Assert.Equal("AV1", document.RootElement.GetProperty("initial_codec").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("protocol_version").GetInt32());
        Assert.Equal(paths.SteamRoot, document.RootElement.GetProperty("steam_root").GetString());
        Assert.Equal(paths.SteamVrRoot, document.RootElement.GetProperty("steamvr_root").GetString());
    }

    [Theory]
    [InlineData("frame", true)]
    [InlineData("10.35.78.1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("steam-frame.local", true)]
    [InlineData("", false)]
    [InlineData("-oProxyCommand=calc", false)]
    [InlineData("frame:22", false)]
    [InlineData("user@frame", false)]
    [InlineData("a b", false)]
    [InlineData("a..b", false)]
    [InlineData(".frame", false)]
    public void Frame_address_accepts_a_hostname_or_ip_and_nothing_ssh_could_misread(string host, bool valid) =>
        Assert.Equal(valid, FrameAddress.IsValid(host));

    [Fact]
    public void Connection_needs_a_valid_address_and_the_key_and_pinned_host_key_but_not_the_dongle()
    {
        using var lab = new CodecTestDirectory();
        var key = Path.Combine(lab.Root, "key"); var hosts = Path.Combine(lab.Root, "hosts");
        File.WriteAllText(key, "k"); File.WriteAllText(hosts, "h");
        CodecController.ValidateConnection(new() { SshHost = "frame", SshKey = key, SshKnownHosts = hosts });
        Assert.Throws<InvalidOperationException>(() => CodecController.ValidateConnection(new() { SshHost = "", SshKey = key, SshKnownHosts = hosts }));
        Assert.Throws<InvalidOperationException>(() => CodecController.ValidateConnection(new() { SshHost = "frame", SshKey = key + "x", SshKnownHosts = hosts }));
        Assert.Throws<InvalidOperationException>(() => CodecController.ValidateConnection(new() { SshHost = "frame", SshKey = key, SshKnownHosts = hosts + "x" }));
    }

    [Fact]
    public void Launch_uses_argument_list_and_hidden_pipes_without_password_arguments()
    {
        using var lab = new CodecTestDirectory();
        Directory.CreateDirectory(Path.Combine(lab.Root, "runtime"));
        File.WriteAllText(Path.Combine(lab.Root, "runtime", "python.exe"), "test");
        File.WriteAllText(Path.Combine(lab.Root, "codec_supervisor.py"), "test");
        var info = CodecController.CreateStartInfo(lab.Root, "session path with spaces", "config path with spaces.json");
        Assert.False(info.UseShellExecute); Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
        Assert.Equal(new[] { Path.Combine(lab.Root, "codec_supervisor.py"), "--session-dir", "session path with spaces", "--config", "config path with spaces.json" }, info.ArgumentList);
        Assert.Empty(info.Arguments);
    }

    [Theory]
    [InlineData(1u, "AV1")]
    [InlineData(2u, "H.264")]
    public void Shared_backend_identity_distinguishes_av1_and_h264(uint id, string expected)
    {
        var bytes = new byte[24];
        uint[] fields = [24, 1, id, 90, 100_000_000, 100_000_000];
        for (var i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), fields[i]);
        var identity = PairedCodecIdentity.Read(bytes);
        Assert.Equal(expected, identity.Codec); Assert.Equal(90u, identity.Fps);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 3);
        Assert.Throws<InvalidDataException>(() => PairedCodecIdentity.Read(bytes));
        Assert.Throws<InvalidDataException>(() => PairedCodecIdentity.Read(new byte[24]));
    }

    [Fact]
    public void Requests_reject_invalid_codec_bitrate_and_refresh()
    {
        foreach (var codec in Enum.GetValues<StreamCodec>()) new CodecSwitchRequest(codec, 100, 90).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodecSwitchRequest((StreamCodec)99).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodecSwitchRequest(StreamCodec.Av1, 201).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodecSwitchRequest(StreamCodec.H264, 100, 91).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodecSwitchRequest(StreamCodec.H264, 100, 144).Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(72)]
    [InlineData(80)]
    [InlineData(90)]
    [InlineData(120)]
    public void Refresh_rate_is_the_users_choice_or_kept_as_it_is(int fps)
    {
        foreach (var codec in Enum.GetValues<StreamCodec>()) new CodecSwitchRequest(codec, 100, fps).Validate();
    }

    [Fact]
    public async Task Keeping_a_codec_is_only_offered_for_H264_and_needs_a_kept_session_to_resume()
    {
        var controller = new CodecController(Path.GetTempPath(), Path.GetTempPath(), _ => null, () => 1000);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SwitchAsync(new(StreamCodec.Av1, 100, 90), new(), "pw", keep: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SwitchAsync(new(StreamCodec.Hevc, 100, 90), new(), "pw", keep: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SwitchAsync(new(StreamCodec.H264, 100, 90), new(), "pw", resume: true));
    }

    [Fact]
    public async Task A_kept_session_waiting_for_SteamVR_is_live_and_reports_what_it_keeps()
    {
        using var lab = new CodecTestDirectory();
        lab.Status(state: "waiting", codec: "Unknown");
        var path = Path.Combine(lab.Root, "status.json");
        var node = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(File.ReadAllText(path))!;
        node["keep"] = true; node["refresh_kept"] = true; node["fps"] = 72;
        File.WriteAllText(path, node.ToJsonString());
        var status = await new CodecController(lab.Root, lab.Root, _ => 100, () => 1000).ReadStatusAsync();
        Assert.True(status.Live); Assert.False(status.Busy); Assert.Equal("waiting", status.State);
        Assert.True(status.Keep); Assert.True(status.RefreshKept); Assert.Equal(72, status.Fps);
    }

    [Fact]
    public void Frame_password_round_trips_through_credential_manager_and_can_be_forgotten()
    {
        var store = new FrameCredentialStore("SteamFrameAdvancedOptions/Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(store.Exists); Assert.Null(store.Load());
            store.Save("p\u00e4ssw0rd with spaces");
            Assert.True(store.Exists); Assert.Equal("p\u00e4ssw0rd with spaces", store.Load());
            store.Save("second"); Assert.Equal("second", store.Load());
            store.Delete(); Assert.False(store.Exists);
            store.Delete(); // forgetting twice is harmless
        }
        finally { store.Delete(); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("two\nlines")]
    public void Credential_store_rejects_unusable_passwords(string password)
    {
        Assert.Throws<ArgumentException>(() => new FrameCredentialStore("SteamFrameAdvancedOptions/Test-" + Guid.NewGuid().ToString("N")).Save(password));
    }

    private sealed class CodecTestDirectory : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "steam-frame-codec-test-" + Guid.NewGuid().ToString("N")));
        public CodecTestDirectory() => Directory.CreateDirectory(Root);
        public void Status(double created = 100, double updated = 1000, string state = "ready", string codec = "HEVC", string? operation = null, bool? ok = null, string? error = null, int? maximumH264 = 500, bool rateLive = false)
        {
            var path = Path.Combine(Root, "status.json");
            var temporary = path + ".tmp";
            var value = JsonSerializer.SerializeToNode(new { protocol_version = 1, owner_pid = 123, owner_created_epoch = created,
                updated_epoch = updated, state, active_codec = codec, message = "Test session", operation_id = operation ?? "",
                last_result = ok is null ? null : new { ok = ok.Value, error }, target_mbps = 100, fps = 90, maximum_h264_mbps = maximumH264, rate_live = rateLive })!;
            if (maximumH264 == null) value.AsObject().Remove("maximum_h264_mbps");
            File.WriteAllText(temporary, value.ToJsonString());
            File.Move(temporary, path, true);
        }
        public async Task<string> WaitForRequest()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var directory = Path.Combine(Root, "requests");
                if (Directory.Exists(directory) && Directory.GetFiles(directory, "request-*.json").FirstOrDefault() is { } path) return path;
                await Task.Delay(20);
            }
            throw new TimeoutException("No test request arrived.");
        }
        public void Dispose()
        {
            var allowed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar + "steam-frame-codec-test-";
            if (!Root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup left its dedicated temporary directory.");
            Directory.Delete(Root, true);
        }
    }
}
