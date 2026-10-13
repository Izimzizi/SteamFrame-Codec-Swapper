using System.Text.Json;
using SteamLinkVRSettings.Frame;
using SteamLinkVRSettings.Services;
using SteamLinkVRSettings.ViewModels;

public sealed class PresetAndStockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "slvr-profile-tests-" + Guid.NewGuid().ToString("N"));
    private SettingsService Driver => new() { UserConfigPath = Path.Combine(_root, "config.json"), DefaultsPath = Path.Combine(_root, "defaults.json") };
    private SettingsService Video => new() { UserConfigPath = Driver.UserConfigPath, DefaultsPath = Driver.DefaultsPath, Section = "steamvr" };
    public PresetAndStockTests() => Directory.CreateDirectory(_root);
    private StockProfiles Profiles => new(Driver, Video, Path.Combine(_root, "previous.json"));
    [Fact]
    public async Task StockResetAndUndoPreservePairingAndUnrelatedSettings()
    {
        await File.WriteAllTextAsync(Driver.UserConfigPath, """{"driver_vrlink":{"targetBandwidth":275.0,"enableQoS":true,"unrelatedPairingValue":"keep"},"steamvr":{"allowSupersampleFiltering":false,"preferredRefreshRate":90,"showMirrorView":true},"other_driver":{"calibration":42}}""");
        var preferences = new FramePreferences { SshHost = "paired-device", QualityEnabled = true, RateControl = EncoderRateControl.Vbr, AdvancedEncoding = new() { MaximumQp = 30 } };
        var captured = await Profiles.CaptureAsync(preferences, true); await Profiles.SaveAsync(captured);
        await Profiles.ResetAsync(preferences);
        var driver = await Driver.LoadUserOverridesAsync(); var video = await Video.LoadUserOverridesAsync();
        Assert.Equal("keep", driver["unrelatedPairingValue"]); Assert.DoesNotContain("targetBandwidth", driver.Keys); Assert.DoesNotContain("enableQoS", driver.Keys);
        Assert.DoesNotContain("allowSupersampleFiltering", video.Keys); Assert.DoesNotContain("preferredRefreshRate", video.Keys); Assert.Equal(true, video["showMirrorView"]);
        Assert.Equal("paired-device", preferences.SshHost); Assert.False(preferences.QualityEnabled); Assert.True(preferences.AdvancedEncoding.IsDefault);
        await Profiles.RestoreAsync((await Profiles.ReadAsync())!, preferences);
        Assert.Equal(275d, Convert.ToDouble((await Driver.LoadUserOverridesAsync())["targetBandwidth"])); Assert.True(preferences.QualityEnabled); Assert.Equal(30, preferences.AdvancedEncoding.MaximumQp);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Driver.UserConfigPath)); Assert.Equal(42, json.RootElement.GetProperty("other_driver").GetProperty("calibration").GetInt32());
        Assert.DoesNotContain("Ssh", await File.ReadAllTextAsync(Path.Combine(_root, "previous.json")));
    }
    [Fact]
    public async Task StockResetLeavesTrackingOscAndConnectivitySettingsAlone()
    {
        await File.WriteAllTextAsync(Driver.UserConfigPath, """{"driver_vrlink":{"targetBandwidth":275.0,"useOSC":true,"useOSCFace":true,"OSCInPort":9001,"shareEyeTrackingData":true,"enableHandTracking":false,"enableNAT":false,"allowVolumeSync":false,"wirelessShutdownTimeout":600}}""");
        var preferences = new FramePreferences();
        var captured = await Profiles.CaptureAsync(preferences, false);
        Assert.DoesNotContain(captured.Driver.Keys, SettingOwnership.UnrelatedKeys.Contains);
        await Profiles.ResetAsync(preferences);
        var driver = await Driver.LoadUserOverridesAsync();
        Assert.DoesNotContain("targetBandwidth", driver.Keys);
        Assert.Equal(true, driver["useOSC"]); Assert.Equal(true, driver["useOSCFace"]); Assert.Equal(9001, driver["OSCInPort"]); Assert.Equal(true, driver["shareEyeTrackingData"]);
        Assert.Equal(false, driver["enableHandTracking"]); Assert.Equal(false, driver["enableNAT"]); Assert.Equal(false, driver["allowVolumeSync"]); Assert.Equal(600, driver["wirelessShutdownTimeout"]);
    }
    [Fact]
    public async Task OlderPresetsWithNonTuningSettingsStillLoadWithoutThem()
    {
        var path = Path.Combine(_root, "legacy-presets.json");
        await File.WriteAllTextAsync(path, """[{"Id":"PRESET-ID","Name":"Legacy","Profile":{"Version":1,"Driver":{"targetBandwidth":200.0,"useOSC":false},"Video":{},"Codec":0,"TargetMbps":200,"Fps":90,"GazeOffset":-4,"PeripheralOffset":8,"Advanced":{}}}]""".Replace("PRESET-ID", Guid.NewGuid().ToString("D")));
        var preset = Assert.Single(await new PresetStore(path).ReadAsync());
        Assert.Equal(new[] { "targetBandwidth" }, preset.Profile.Driver.Keys);
    }
    [Fact]
    public async Task OutOfRangeOlderOverridesDoNotBlockBackToStockOrSavingAPreset()
    {
        await File.WriteAllTextAsync(Driver.UserConfigPath, """{"driver_vrlink":{"targetBandwidth":600.0,"enableQoS":true}}""");
        var captured = await Profiles.CaptureAsync(new FramePreferences(), false);
        Assert.DoesNotContain("targetBandwidth", captured.Driver.Keys); Assert.Contains("enableQoS", captured.Driver.Keys);
        await Profiles.SaveAsync(captured);
        await new PresetStore(Path.Combine(_root, "presets.json")).SaveAsync("From old release", captured);
    }
    [Fact]
    public async Task CompletePresetSurvivesRestartAndNameReplacement()
    {
        var path = Path.Combine(_root, "presets.json"); var store = new PresetStore(path);
        var original = new TuningProfile { Codec = StreamCodec.Av1, TargetMbps = 100, Fps = 90, QualityEnabled = true, GazeOffset = -12, PeripheralOffset = 20, BitratePatch = true,
            RateControl = EncoderRateControl.Cbr, MultiPass = EncoderMultiPass.FullResolution, Advanced = new() { MaximumQp = 24, VbvMbit = 2, StrictGopTarget = false, LowDelayKeyFrameScale = 2 },
            Driver = new() { ["targetBandwidth"] = JsonSerializer.SerializeToElement(100d), ["enableQoS"] = JsonSerializer.SerializeToElement(true) }, Video = new() { ["allowSupersampleFiltering"] = JsonSerializer.SerializeToElement(false) } };
        await store.SaveAsync("Balanced", original); var reopened = new PresetStore(path); var first = Assert.Single(await reopened.ReadAsync());
        Assert.Equal(original.Advanced, first.Profile.Advanced); Assert.Equal(original.GazeOffset, first.Profile.GazeOffset); Assert.True(first.Profile.BitratePatch);
        Assert.Equal(EncoderMultiPass.FullResolution, first.Profile.MultiPass);
        await reopened.SaveAsync("BALANCED", original with { Fps = 120 }); var updated = Assert.Single(await store.ReadAsync()); Assert.Equal(first.Id, updated.Id); Assert.Equal(120, updated.Profile.Fps);
    }
    [Fact]
    public async Task BuiltInHighQualityRestoresCompleteSetupAndKeepsPairing()
    {
        await File.WriteAllTextAsync(Driver.UserConfigPath, """{"driver_vrlink":{"asyncSend":false,"unrelatedPairingValue":"keep"},"steamvr":{"preferredRefreshRate":120,"showMirrorView":true}}""");
        var presets = BuiltInPresets.Create();
        Assert.Equal(3, presets.Select(p => p.Id).Distinct().Count());
        foreach (var entry in presets) { StockProfiles.Validate(entry.Profile); Assert.True(entry.IsBuiltIn); }
        var preset = Assert.Single(presets, p => p.Name == "High quality HEVC");
        StockProfiles.Validate(preset.Profile);
        var preferences = new FramePreferences { SshHost = "paired-device" };
        await Profiles.RestoreAsync(preset.Profile, preferences);
        var driver = await Driver.LoadUserOverridesAsync(); var video = await Video.LoadUserOverridesAsync();
        Assert.Equal(400d, Convert.ToDouble(driver["targetBandwidth"]));
        Assert.Equal(2048, Convert.ToInt32(driver["streamFormatWidth"]));
        Assert.Equal(true, driver["asyncSend"]); Assert.Equal(true, driver["10bit"]);
        Assert.Equal(false, driver["automaticBandwidth"]); Assert.Equal(false, driver["automaticStreamFormatWidth"]);
        Assert.Equal(false, driver["allowMultipleLinks"]);
        Assert.Equal(90d, Convert.ToDouble(video["preferredRefreshRate"])); Assert.Equal(false, video["allowSupersampleFiltering"]);
        Assert.Equal(StreamCodec.Hevc, preferences.RequestedCodec); Assert.Equal(400, preferences.CodecTargetMbps); Assert.Equal(90, preferences.CodecFps);
        Assert.Equal(EncoderRateControl.Cbr, preferences.RateControl); Assert.Equal(EncoderMultiPass.QuarterResolution, preferences.MultiPass);
        Assert.True(preferences.QualityEnabled); Assert.Equal(-2, preferences.GazeOffset); Assert.Equal(-6, preferences.PeripheralOffset);
        Assert.True(preferences.ReapplyAfterRestart); Assert.True(preferences.AdvancedEncoding.IsDefault); Assert.True(preset.Profile.BitratePatch);
        Assert.Equal("paired-device", preferences.SshHost); Assert.Equal("keep", driver["unrelatedPairingValue"]); Assert.Equal(true, video["showMirrorView"]);
    }
    [Fact]
    public async Task SavingEditedBuiltInDoesNotReplaceAppPresetOrOtherSavedPresets()
    {
        var path = Path.Combine(_root, "presets.json"); var store = new PresetStore(path);
        Assert.Empty(await store.ReadAsync());
        var builtIns = BuiltInPresets.Create();
        var preset = Assert.Single(builtIns, p => p.Name == "High quality HEVC");
        Assert.True(preset.IsBuiltIn); Assert.False(File.Exists(path));
        await store.SaveAsync("Existing", new());
        preset.Profile.Driver["targetBandwidth"] = JsonSerializer.SerializeToElement(350d);
        preset.Profile.Driver["asyncSend"] = JsonSerializer.SerializeToElement(false);
        preset.Profile.Video["preferredRefreshRate"] = JsonSerializer.SerializeToElement(120d);
        await store.SaveAsync(preset.Name, preset.Profile with { TargetMbps = 350, Fps = 120 });
        var saved = await new PresetStore(path).ReadAsync();
        Assert.Equal(2, saved.Count); Assert.Contains(saved, p => p.Name == "Existing");
        var edited = Assert.Single(saved, p => p.Name == preset.Name);
        Assert.False(edited.IsBuiltIn); Assert.Equal(350, edited.Profile.TargetMbps); Assert.Equal(120, edited.Profile.Fps);
        Assert.False(edited.Profile.Driver["asyncSend"].GetBoolean());
        var fresh = Assert.Single(BuiltInPresets.Create(), p => p.Id == preset.Id);
        Assert.Equal(preset.Id, fresh.Id); Assert.NotEqual(fresh.Id, edited.Id);
        Assert.Equal(400, fresh.Profile.TargetMbps); Assert.Equal(400d, fresh.Profile.Driver["targetBandwidth"].GetDouble());
        Assert.True(fresh.Profile.Driver["asyncSend"].GetBoolean()); Assert.Equal(90d, fresh.Profile.Video["preferredRefreshRate"].GetDouble());
        Assert.DoesNotContain("IsBuiltIn", await File.ReadAllTextAsync(path));
        // Editing one built-in cannot alter the other profiles in the same catalog.
        var full = Assert.Single(builtIns, p => p.Profile.MultiPass == EncoderMultiPass.FullResolution);
        Assert.Equal(400d, full.Profile.Driver["targetBandwidth"].GetDouble());
        Assert.True(full.Profile.Driver["asyncSend"].GetBoolean());
        Assert.Equal(90d, full.Profile.Video["preferredRefreshRate"].GetDouble());
        var smooth = Assert.Single(builtIns, p => p.Name == "Smooth HEVC");
        // Applying the fallback clears previous experimental fields and custom gaze bias.
        var preferences = new FramePreferences { AdvancedEncoding = new() { StrictGopTarget = true, LowDelayKeyFrameScale = 4 } };
        await Profiles.RestoreAsync(full.Profile, preferences);
        await Profiles.RestoreAsync(smooth.Profile, preferences);
        Assert.Equal(300, preferences.CodecTargetMbps); Assert.False(preferences.QualityEnabled);
        Assert.Equal(EncoderRateControl.Vbr, preferences.RateControl); Assert.Equal(EncoderMultiPass.SinglePass, preferences.MultiPass);
        Assert.True(preferences.AdvancedEncoding.IsDefault);
        Assert.Equal(false, (await Driver.LoadUserOverridesAsync())["asyncSend"]);
    }
    [Fact]
    public void PresetNumbersKeepTheSettingsApisFloatAndIntegerTypes()
    {
        var values = StockProfiles.Deserialize(new() { ["targetBandwidth"] = JsonSerializer.SerializeToElement(200d), ["streamFormatWidth"] = JsonSerializer.SerializeToElement(1536), ["enableTimedRetry"] = JsonSerializer.SerializeToElement(1), ["backoffRecoveryCoefficient"] = JsonSerializer.SerializeToElement(1.5) });
        Assert.IsType<double>(values["targetBandwidth"]); Assert.IsType<int>(values["streamFormatWidth"]); Assert.IsType<int>(values["enableTimedRetry"]); Assert.IsType<double>(values["backoffRecoveryCoefficient"]);
    }
    [Fact]
    public async Task AdditionalEditorDoesNotExposeOrResetGuidedSettings()
    {
        await File.WriteAllTextAsync(Driver.DefaultsPath, """{"driver_vrlink":{"targetBandwidth":150.0,"enableQoS":true,"OSCInPort":9000}}""");
        await File.WriteAllTextAsync(Driver.UserConfigPath, """{"driver_vrlink":{"targetBandwidth":200.0,"enableQoS":false,"OSCInPort":9001}}""");
        var vm = new SettingsViewModel(Driver, new NoLive(), SettingOwnership.GuidedKeys); await vm.LoadCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Categories.SelectMany(x => x.Settings), x => SettingOwnership.GuidedKeys.Contains(x.Key));
        vm.ResetAllToDefaultsCommand.Execute(null); await vm.SaveCommand.ExecuteAsync(null);
        var values = await Driver.LoadUserOverridesAsync(); Assert.Equal(200d, Convert.ToDouble(values["targetBandwidth"])); Assert.Equal(false, values["enableQoS"]);
    }
    [Fact]
    public async Task UnknownKeysAreRejectedBeforeReplacingTheCatalog()
    {
        var path = Path.Combine(_root, "presets.json"); var store = new PresetStore(path); await store.SaveAsync("Good", new());
        var before = await File.ReadAllTextAsync(path);
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync("Bad", new() { Driver = new() { ["unrelatedPairingValue"] = JsonSerializer.SerializeToElement("change") } }));
        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }
    [Fact]
    public void FullPresetAcceptsHighBitrateAndTransportMode()
    {
        StockProfiles.Validate(new() { TargetMbps = 500, Driver = new() { ["targetBandwidth"] = JsonSerializer.SerializeToElement(500d), ["reqEncMode"] = JsonSerializer.SerializeToElement("aes") } });
        StockProfiles.Validate(new() { Codec = StreamCodec.H264, TargetMbps = 500 });
        Assert.Throws<IOException>(() => StockProfiles.Validate(new() { Codec = StreamCodec.Av1, TargetMbps = 201 }));
    }
    [Fact]
    public async Task InvalidValuesCannotReplaceSavedPresets()
    {
        var path = Path.Combine(_root, "validated.json"); var store = new PresetStore(path); await store.SaveAsync("Good", new());
        var before = await File.ReadAllTextAsync(path);
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync("Bad", new() { Driver = new() { ["streamFormatWidth"] = JsonSerializer.SerializeToElement(511) } }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("Fraction", new() { Driver = new() { ["enableTimedRetry"] = JsonSerializer.SerializeToElement(.5) } }));
        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }
    private sealed class NoLive : ILiveSettings { public bool IsRunning => false; public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, IReadOnlyCollection<string> removeKeys) => throw new InvalidOperationException(); }
    public void Dispose()
    {
        var target = Path.GetFullPath(_root);
        if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "slvr-profile-tests-", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected test directory.");
        Directory.Delete(target, true);
    }
}
