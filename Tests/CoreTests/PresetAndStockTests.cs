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
    public async Task CompletePresetSurvivesRestartAndNameReplacement()
    {
        var path = Path.Combine(_root, "presets.json"); var store = new PresetStore(path);
        var original = new TuningProfile { Codec = StreamCodec.Av1, TargetMbps = 100, Fps = 90, QualityEnabled = true, GazeOffset = -12, PeripheralOffset = 20, BitratePatch = true,
            RateControl = EncoderRateControl.Vbr, MultiPass = EncoderMultiPass.SinglePass, Advanced = new() { MaximumQp = 24, VbvMbit = 2 },
            Driver = new() { ["targetBandwidth"] = JsonSerializer.SerializeToElement(100d), ["enableQoS"] = JsonSerializer.SerializeToElement(true) }, Video = new() { ["allowSupersampleFiltering"] = JsonSerializer.SerializeToElement(false) } };
        await store.SaveAsync("Balanced", original); var reopened = new PresetStore(path); var first = Assert.Single(await reopened.ReadAsync());
        Assert.Equal(original.Advanced, first.Profile.Advanced); Assert.Equal(original.GazeOffset, first.Profile.GazeOffset); Assert.True(first.Profile.BitratePatch);
        await reopened.SaveAsync("BALANCED", original with { Fps = 120 }); var updated = Assert.Single(await store.ReadAsync()); Assert.Equal(first.Id, updated.Id); Assert.Equal(120, updated.Profile.Fps);
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
