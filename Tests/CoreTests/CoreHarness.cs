using System.Text.Json;
using System.Text.Json.Nodes;
using SteamLinkVRSettings.Frame;
using SteamLinkVRSettings.Services;

namespace SteamLinkVRSettings.Tests;

// Former console harness; xUnit runs it through a [Fact] so it shows in Test Explorer and dotnet test.
internal static class CoreHarness
{
    public static async Task<List<string>> RunAsync()
    {
        // These tests never open SteamVR, ADB, SSH, or the user's Steam configuration.
        var workspace = Path.Combine(Path.GetTempPath(), "SteamLinkVRSettings-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        int passed = 0, failed = 0;
        var failures = new List<string>();
        try
        {
            await Test("changed setting preserves unrelated sections and structured unknown values", async () =>
            {
                var service = Service("preserve");
                const string original = """
                    {
                      // SteamVR accepts comments and trailing commas.
                      "steamvr": { "custom": [false, "unchanged", {"n": 7}], },
                      "LastKnown": { "model": "Frame", "future": null },
                      "driver_vrlink": {
                        "targetBandwidth": 300,
                        "futureObject": {"switch": true, "list": [1, 2, null]},
                        "futureArray": ["keep", {"another": 3}],
                        "unknownText": "keep this text",
                        "futureNull": null,
                      },
                    }
                    """;
                await File.WriteAllTextAsync(service.UserConfigPath, original);
                var before = Parse(original);
                await service.SaveUserOverridesAsync(new() { ["targetBandwidth"] = 350.0, ["automaticBandwidth"] = false });
                var after = Parse(await File.ReadAllTextAsync(service.UserConfigPath));
                Equal(350, after["driver_vrlink"]!["targetBandwidth"]!.GetValue<double>());
                Check(JsonNode.DeepEquals(before["steamvr"], after["steamvr"]), "unrelated section changed");
                Check(JsonNode.DeepEquals(before["LastKnown"], after["LastKnown"]), "device metadata changed");
                foreach (var key in new[] { "futureObject", "futureArray", "unknownText", "futureNull" })
                    Check(JsonNode.DeepEquals(before["driver_vrlink"]![key], after["driver_vrlink"]![key]), $"{key} changed");
                Check(after["driver_vrlink"]!["automaticBandwidth"]!.GetValue<bool>() == false, "boolean saved with wrong type");
            });
            await Test("structured unknown values do not prevent loading ordinary controls", async () =>
            {
                var service = Service("structured-load");
                await File.WriteAllTextAsync(service.UserConfigPath, """{"driver_vrlink":{"targetBandwidth":300,"newObject":{"enabled":true},"newArray":[1,2]}}""");
                var values = await service.LoadUserOverridesAsync();
                Equal(300, Convert.ToDouble(values["targetBandwidth"]));
                Check(values.ContainsKey("newObject") && values.ContainsKey("newArray"), "unknown values vanished during loading");
                await service.SaveUserOverridesAsync(values);
                await service.SaveUserOverridesAsync(new() { ["allowMultipleLinks"] = true });
                var root = Parse(await File.ReadAllTextAsync(service.UserConfigPath));
                Check(root["driver_vrlink"]!["newObject"] is JsonObject && root["driver_vrlink"]!["newArray"] is JsonArray, "unknown JSON shapes changed");
            });
            await Test("SteamVR video override uses its section and preserves driver controls", async () =>
            {
                var driver = Service("video-section");
                const string original = """{"steamvr":{"allowSupersampleFiltering":true,"other":7},"driver_vrlink":{"targetBandwidth":200},"LastKnown":{"model":"Frame"}}""";
                await File.WriteAllTextAsync(driver.UserConfigPath, original);
                var video = new SettingsService { UserConfigPath = driver.UserConfigPath, DefaultsPath = driver.DefaultsPath, Section = OpenVrSettings.SteamVrVideoSection };
                await video.SaveUserOverridesAsync(new() { [OpenVrSettings.SupersampleFilteringKey] = false });
                var before = Parse(original); var after = Parse(await File.ReadAllTextAsync(driver.UserConfigPath));
                Check(!after["steamvr"]!["allowSupersampleFiltering"]!.GetValue<bool>(), "video toggle was not saved");
                Check(JsonNode.DeepEquals(before["driver_vrlink"], after["driver_vrlink"]), "video toggle changed driver controls");
                Check(JsonNode.DeepEquals(before["LastKnown"], after["LastKnown"]), "video toggle changed headset metadata");
                Check((await video.LoadUserOverridesAsync())[OpenVrSettings.SupersampleFilteringKey] is false, "saved video readback was incorrect");
                Check(!(await driver.LoadUserOverridesAsync()).ContainsKey(OpenVrSettings.SupersampleFilteringKey), "video toggle leaked into the driver section");
            });
            await Test("atomic save backs up the exact previous bytes and replaces the complete document", async () =>
            {
                var service = Service("backup");
                const string original = "{\r\n \"other\": {\"persist\": true}, \"driver_vrlink\": {\"targetBandwidth\": 250}\r\n}\r\n";
                await File.WriteAllTextAsync(service.UserConfigPath, original);
                await service.SaveUserOverridesAsync(new() { ["targetBandwidth"] = 300.0 });
                Check(await File.ReadAllTextAsync(service.UserConfigPath + ".SteamLinkVRSettings.bak") == original, "backup is not exact pre-save bytes");
                var firstSaved = await File.ReadAllTextAsync(service.UserConfigPath);
                await service.SaveUserOverridesAsync(new() { ["targetBandwidth"] = 325.0 });
                Check(await File.ReadAllTextAsync(service.UserConfigPath + ".SteamLinkVRSettings.bak") == firstSaved, "second backup is not previous complete revision");
                Equal(325, Parse(await File.ReadAllTextAsync(service.UserConfigPath))["driver_vrlink"]!["targetBandwidth"]!.GetValue<double>());
                Check(!Directory.EnumerateFiles(Path.GetDirectoryName(service.UserConfigPath)!, "*.tmp").Any(), "temporary save files remain");
            });
            await Test("first save creates missing directories without inventing a previous revision", async () =>
            {
                var service = Service("new", createDirectory: false);
                await service.SaveUserOverridesAsync(new() { ["allowMultipleLinks"] = true });
                Check(Parse(await File.ReadAllTextAsync(service.UserConfigPath))["driver_vrlink"]!["allowMultipleLinks"]!.GetValue<bool>(), "new config invalid");
                Check(!File.Exists(service.UserConfigPath + ".SteamLinkVRSettings.bak"), "backup fabricated for nonexistent original");
            });
            await Test("malformed config is preserved when save refuses to parse it", async () =>
            {
                var service = Service("invalid");
                const string broken = "{\"driver_vrlink\": {\"targetBandwidth\": 300, BROKEN";
                await File.WriteAllTextAsync(service.UserConfigPath, broken);
                await MustThrow(() => service.SaveUserOverridesAsync(new() { ["targetBandwidth"] = 350.0 }));
                Check(await File.ReadAllTextAsync(service.UserConfigPath) == broken, "invalid config was overwritten");
                Check(!File.Exists(service.UserConfigPath + ".SteamLinkVRSettings.bak"), "refused save changed backup");
            });
            await Test("incompatible root/section shapes are refused without destroying their bytes", async () =>
            {
                foreach (var text in new[] { "[]", "{\"driver_vrlink\":[1,2]}" })
                {
                    var service = Service("wrong-shape-" + Guid.NewGuid().ToString("N"));
                    await File.WriteAllTextAsync(service.UserConfigPath, text);
                    await MustThrow(() => service.SaveUserOverridesAsync(new() { ["targetBandwidth"] = 300.0 }));
                    Check(await File.ReadAllTextAsync(service.UserConfigPath) == text, "wrong-shaped config overwritten");
                }
            });
            await Test("defaults and overrides merge with their primitive types intact", async () =>
            {
                var service = Service("merge");
                await File.WriteAllTextAsync(service.DefaultsPath, """{"driver_vrlink":{"targetBandwidth":232,"automaticBandwidth":true,"reqEncMode":"auto","floatValue":0.75,"nullable":null}}""");
                await File.WriteAllTextAsync(service.UserConfigPath, """{"driver_vrlink":{"targetBandwidth":300,"automaticBandwidth":false}}""");
                var values = await service.LoadMergedAsync();
                Equal(300, Convert.ToDouble(values["targetBandwidth"]));
                Check(values["automaticBandwidth"] is false && values["reqEncMode"] is "auto", "override/default merge invalid");
                Check(values["floatValue"] is double d && d == .75 && values["nullable"] is null, "primitive types changed");
            });
            await Test("concurrent independent saves preserve every writer's setting", async () =>
            {
                var service = Service("concurrent");
                await File.WriteAllTextAsync(service.UserConfigPath, JsonSerializer.Serialize(new { unrelated = new string('x', 1_000_000), driver_vrlink = new { targetBandwidth = 300 } }));
                await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
                    new SettingsService { UserConfigPath = service.UserConfigPath, DefaultsPath = service.DefaultsPath }
                        .SaveUserOverridesAsync(new() { ["writer" + i] = i })));
                var section = Parse(await File.ReadAllTextAsync(service.UserConfigPath))["driver_vrlink"]!;
                for (int i = 0; i < 12; i++) Check(section["writer" + i]?.GetValue<int>() == i, $"writer {i} was lost");
            });

            await Test("Steam path properties resolve config and driver inside different installations", () =>
            {
                var steam = Path.Combine(workspace, "Steam with spaces");
                var vr = Path.Combine(workspace, "Other library", "SteamVR");
                var paths = new SteamPaths(steam, vr);
                Check(paths.Config == Path.Combine(steam, "config", "steamvr.vrsettings"), "config used runtime library instead of Steam installation");
                Check(paths.Driver == Path.Combine(vr, "drivers", "vrlink", "bin", "win64", "driver_vrlink.dll"), "driver ignored separate runtime");
                Check(paths.Defaults.StartsWith(vr + Path.DirectorySeparatorChar) && paths.OpenVrApi.StartsWith(vr + Path.DirectorySeparatorChar), "runtime resources used Steam root");
                return Task.CompletedTask;
            });
            await Test("preferences are safe by default and preserve opt-in quality across serialization", () =>
            {
                var defaults = new FramePreferences();
                Check(!defaults.QualityEnabled && !defaults.ReapplyAfterRestart && defaults.SshHost == "" && defaults.AdbSerial == "", "default preferences silently enabled hooks or selected a device");
                var configured = new FramePreferences { AdbPath = @"D:\Platform tools\adb.exe", AdbSerial = "selected-frame", SshHost = "frame.example", SshKey = @"D:\Paired keys\devkit_rsa", GazeOffset = -4, PeripheralOffset = 8, QualityEnabled = true, ReapplyAfterRestart = true };
                var restored = JsonSerializer.Deserialize<FramePreferences>(JsonSerializer.Serialize(configured))!;
                Check(restored.AdbPath == configured.AdbPath && restored.AdbSerial == configured.AdbSerial && restored.SshKey == configured.SshKey && restored.SshHost == configured.SshHost, "connection preferences failed round trip");
                Check(restored.QualityEnabled && restored.ReapplyAfterRestart && restored.GazeOffset == -4 && restored.PeripheralOffset == 8, "quality/reapply preferences failed round trip");
                return Task.CompletedTask;
            });
            await Test("preferences save and load isolated files without partial JSON or staging leftovers", async () =>
            {
                var path = Path.Combine(workspace, "preferences", "frame.json");
                var configured = new FramePreferences { AdbSerial = "paired-frame", SshHost = "frame.example", QualityEnabled = true, ReapplyAfterRestart = true, GazeOffset = -4, PeripheralOffset = 8 };
                await configured.SaveAsync(path);
                var loaded = FramePreferences.Load(path);
                Check(loaded.AdbSerial == "paired-frame" && loaded.SshHost == "frame.example" && loaded.QualityEnabled && loaded.ReapplyAfterRestart, "saved preferences missing");
                configured.GazeOffset = -6;
                configured.ReapplyAfterRestart = false;
                await configured.SaveAsync(path);
                loaded = FramePreferences.Load(path);
                Check(loaded.GazeOffset == -6 && !loaded.ReapplyAfterRestart && loaded.PeripheralOffset == 8, "atomic replacement lost updated or unrelated preferences");
                Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "preferences staging files remain");
                Parse(await File.ReadAllTextAsync(path));
            });
            await Test("missing or malformed preferences fall back to disabled quality and reapply", async () =>
            {
                var path = Path.Combine(workspace, "bad-preferences.json");
                var missing = FramePreferences.Load(path);
                Check(!missing.QualityEnabled && !missing.ReapplyAfterRestart, "missing preferences enabled hooks");
                const string broken = "{\"QualityEnabled\": true, \"ReapplyAfterRestart\": true,";
                await File.WriteAllTextAsync(path, broken);
                var malformed = FramePreferences.Load(path);
                Check(!malformed.QualityEnabled && !malformed.ReapplyAfterRestart, "malformed preferences enabled hooks");
                Check(await File.ReadAllTextAsync(path) == broken, "loading preferences silently rewrote them");
            });

            await Test("rate requires two observations then measures encoded byte deltas", () =>
            {
                var tracker = new RateTracker();
                Gap(tracker.Sample("session-a", 100, 1, 100));
                var rate = tracker.Sample("session-a", 37_500_100, 91, 101);
                Equal(300, rate.Mbps); Equal(90, rate.Fps);
                return Task.CompletedTask;
            });
            await Test("requested target is independent of actual compressed output", async () =>
            {
                var service = Service("target-versus-actual");
                await service.SaveUserOverridesAsync(new() { ["targetBandwidth"] = 300.0 });
                var values = await service.LoadUserOverridesAsync();
                var tracker = new RateTracker();
                Gap(tracker.Sample("video", 0, 0, 10));
                var actual = tracker.Sample("video", 2_500_000, 90, 11);
                Equal(300, Convert.ToDouble(values["targetBandwidth"]));
                Equal(20, actual.Mbps); Equal(90, actual.Fps);
                Check(actual.Mbps != Convert.ToDouble(values["targetBandwidth"]), "target substituted for encoded bitrate");
            });
            await Test("rates use elapsed time rather than assuming a one-second poll", () =>
            {
                var tracker = new RateTracker();
                Gap(tracker.Sample("timing", 0, 0, 20));
                var actual = tracker.Sample("timing", 50_000_000, 180, 22.5);
                Equal(160, actual.Mbps); Equal(72, actual.Fps);
                return Task.CompletedTask;
            });
            await Test("short interval observations accumulate into the next usable interval", () =>
            {
                var tracker = new RateTracker();
                Gap(tracker.Sample("timing", 0, 0, 10));
                Gap(tracker.Sample("timing", 1_000_000, 5, 10.05));
                var actual = tracker.Sample("timing", 10_000_000, 45, 10.5);
                Equal(160, actual.Mbps); Equal(90, actual.Fps);
                return Task.CompletedTask;
            });
            await Test("a new encoder generation resets counters even when they are greater", () =>
            {
                var tracker = new RateTracker();
                Gap(tracker.Sample("old", 0, 0, 10));
                Equal(8, tracker.Sample("old", 1_000_000, 1, 11).Mbps);
                Gap(tracker.Sample("new", 9_000_000, 9, 12));
                Equal(16, tracker.Sample("new", 11_000_000, 11, 13).Mbps);
                return Task.CompletedTask;
            });
            await Test("counter rollback and explicit reset cannot fabricate a spike", () =>
            {
                var tracker = new RateTracker();
                Gap(tracker.Sample("same", 100_000_000, 900, 10));
                Gap(tracker.Sample("same", 0, 901, 11));
                Equal(8, tracker.Sample("same", 1_000_000, 902, 12).Mbps);
                Gap(tracker.Sample("same", 2_000_000, 1, 13));
                Equal(8, tracker.Sample("same", 3_000_000, 2, 14).Mbps);
                tracker.Reset();
                Gap(tracker.Sample("same", 4_000_000, 3, 15));
                return Task.CompletedTask;
            });
            await Test("backward or repeated timestamps are gaps, not infinite/negative rates", () =>
            {
                var tracker = new RateTracker();
                Gap(tracker.Sample("clock", 0, 0, 10));
                Gap(tracker.Sample("clock", 1_000_000, 1, 10));
                Gap(tracker.Sample("clock", 2_000_000, 2, 9));
                Equal(8, tracker.Sample("clock", 3_000_000, 3, 10).Mbps);
                return Task.CompletedTask;
            });
            await Test("invalid timestamps reset the baseline and never publish a value", () =>
            {
                foreach (var invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                {
                    var tracker = new RateTracker();
                    Gap(tracker.Sample("clock", 0, 0, 10));
                    Gap(tracker.Sample("clock", 1_000_000, 1, invalid));
                    Gap(tracker.Sample("clock", 2_000_000, 2, 11));
                    Equal(8, tracker.Sample("clock", 3_000_000, 3, 12).Mbps);
                }
                return Task.CompletedTask;
            });
            await Test("stopped frame production becomes unavailable then resumes from recent deltas", () =>
            {
                var tracker = new RateTracker();
                Gap(tracker.Sample("stall", 0, 0, 10));
                var healthy = tracker.Sample("stall", 1_000_000, 90, 11);
                Equal(8, healthy.Mbps); Equal(90, healthy.Fps);
                var idle = tracker.Sample("stall", 1_000_000, 90, 12);
                Equal(0, idle.Mbps); Equal(0, idle.Fps);
                Gap(tracker.Sample("stall", 1_000_000, 90, 14.1));
                Gap(tracker.Sample("stall", 1_000_000, 90, 15.1));
                var resumed = tracker.Sample("stall", 2_000_000, 180, 16.1);
                Equal(8, resumed.Mbps); Equal(90, resumed.Fps);
                return Task.CompletedTask;
            });
            await Test("large cumulative counters are reduced before conversion to a rate", () =>
            {
                var tracker = new RateTracker();
                Gap(tracker.Sample("large", ulong.MaxValue - 2_000_000, ulong.MaxValue - 100, 10));
                var actual = tracker.Sample("large", ulong.MaxValue - 1_000_000, ulong.MaxValue - 10, 11);
                Equal(8, actual.Mbps); Equal(90, actual.Fps);
                return Task.CompletedTask;
            });
            await Test("adapter matching excludes ordinary Wi-Fi and filter duplicates", () =>
            {
                Check(FrameTrafficMonitor.Classify("Realtek 8832CU Wireless LAN WiFi 6 USB NIC For Valve") == 2, "Valve dongle missing");
                Check(FrameTrafficMonitor.Classify("Killer Wi-Fi 6E AX1675x") is null, "ordinary Wi-Fi misidentified");
                Check(FrameTrafficMonitor.Classify("UsbNcm") == 1, "USB NCM missing");
                Check(FrameTrafficMonitor.Classify("UsbNcm Packet Scheduler") is null, "filter duplicate accepted");
                return Task.CompletedTask;
            });
            await Test("video path uses headset status rather than the Wi-Fi toggle or combined link", () =>
            {
                var links = new FrameLink[] { new(255, true, 3, 0), new(1, false, 0, 0), new(2, true, 3, 0), new(3, false, 0, 0) };
                var status = new ConnectionSnapshot(true, true, links, "test");
                Check(status.VideoPath == "Dedicated wireless" && !status.WiredActive, "wrong route");
                Check((status with { HasInfo = false }).VideoPath == "Unknown", "unavailable status retained an active route");
                Check((status with { Links = [new(255, true, 0, 0)] }).VideoPath == "Inactive", "combined link treated as physical route");
                return Task.CompletedTask;
            });
            await Test("traffic graph selects the dongle only when the headset confirms that route", () =>
            {
                var adapters = new AdapterSample[] { new("dongle", "10.35.78.21", true, 125, 2, 2), new("USB", "10.1.1.1", true, 300, 4, 1) };
                var status = new ConnectionSnapshot(true, true, [new(2, true, 3, 0)], "test");
                Equal(125, FrameTrafficMonitor.ActiveSendMbps(status, adapters));
                Check(FrameTrafficMonitor.ActiveSendMbps(status with { HasInfo = false }, adapters) is null, "adapter presence established a route");
                Check(FrameTrafficMonitor.ActiveSendMbps(status with { Links = [new(3, true, 3, 0)] }, adapters) is null, "local network incorrectly used dongle counters");
                return Task.CompletedTask;
            });
            await Test("multiple links or matching adapters cannot fabricate an aggregate rate", () =>
            {
                var adapters = new AdapterSample[] { new("dongle", "10.35.78.21", true, 125, 2, 2), new("duplicate", "10.35.78.22", true, 140, 2, 2) };
                var status = new ConnectionSnapshot(true, true, [new(2, true, 3, 0)], "test");
                Check(FrameTrafficMonitor.ActiveSendMbps(status, adapters) is null, "ambiguous dongle counters accepted");
                Check((status with { Links = [new(1, true, 1, 0), new(2, true, 3, 0)] }).VideoPath == "Multiple links", "multiple routes hidden");
                Check(FrameTrafficMonitor.ActiveSendMbps(status with { Links = [new(1, true, 1, 0), new(2, true, 3, 0)] }, adapters) is null, "different paths summed");
                return Task.CompletedTask;
            });
            await Test("down or addressless adapters are excluded from active traffic", () =>
            {
                var adapters = new AdapterSample[] { new("down", "10.35.78.22", false, 400, 2, 2), new("no address", "No IPv4 address", true, 300, 2, 2), new("active", "10.35.78.21", true, 125, 2, 2) };
                Equal(125, FrameTrafficMonitor.ActiveSendMbps(new(true, true, [new(2, true, 3, 0)], "test"), adapters));
                return Task.CompletedTask;
            });
        }
        finally
        {
            var safeRoot = Path.GetFullPath(workspace);
            if (safeRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "SteamLinkVRSettings-core-", StringComparison.OrdinalIgnoreCase))
                Directory.Delete(safeRoot, recursive: true);
        }
        Console.WriteLine($"{passed} passed; {failed} failed.");
        return failures;

        SettingsService Service(string name, bool createDirectory = true)
        {
            var directory = Path.Combine(workspace, name);
            if (createDirectory) Directory.CreateDirectory(directory);
            return new() { UserConfigPath = Path.Combine(directory, "steamvr.vrsettings"), DefaultsPath = Path.Combine(directory, "default.vrsettings") };
        }
        async Task Test(string name, Func<Task> body)
        {
            try { await body(); Console.WriteLine("PASS " + name); passed++; }
            catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failures.Add(name + ": " + ex.Message); failed++; }
        }
        static JsonNode Parse(string json) => JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })!;
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Equal(double expected, double? actual) => Check(actual.HasValue && double.IsFinite(actual.Value) && Math.Abs(expected - actual.Value) < 0.00001, $"expected {expected}, got {actual?.ToString() ?? "unavailable"}");
        static void Gap((double? Mbps, double? Fps) rate) => Check(rate.Mbps is null && rate.Fps is null, $"expected gap, got {rate}");
        static async Task MustThrow(Func<Task> operation)
        {
            try { await operation(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException) { return; }
            throw new Exception("unsafe save was accepted");
        }

    }
}
