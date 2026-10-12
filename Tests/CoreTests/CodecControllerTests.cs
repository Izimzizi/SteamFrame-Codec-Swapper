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
        Assert.False((await controller.ReadStatusAsync()).CanStart);
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
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodecSwitchRequest(StreamCodec.H264, 100, 80).Validate());
    }

    private sealed class CodecTestDirectory : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "steam-frame-codec-test-" + Guid.NewGuid().ToString("N")));
        public CodecTestDirectory() => Directory.CreateDirectory(Root);
        public void Status(double created = 100, double updated = 1000, string state = "ready", string codec = "HEVC", string? operation = null, bool? ok = null, string? error = null, int? maximumH264 = 500)
        {
            var path = Path.Combine(Root, "status.json");
            var temporary = path + ".tmp";
            var value = JsonSerializer.SerializeToNode(new { protocol_version = 1, owner_pid = 123, owner_created_epoch = created,
                updated_epoch = updated, state, active_codec = codec, message = "Test session", operation_id = operation ?? "",
                last_result = ok is null ? null : new { ok = ok.Value, error }, target_mbps = 100, fps = 90, maximum_h264_mbps = maximumH264 })!;
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
