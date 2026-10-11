using System.Text.Json.Nodes;
using SteamLinkVRSettings.Models;
using SteamLinkVRSettings.Services;
using SteamLinkVRSettings.ViewModels;

namespace SteamLinkVRSettings.Tests;

public class CoreTests
{
    [Fact]
    public async Task SettingsServicePathsPreferencesAndRates()
    {
        var failures = await CoreHarness.RunAsync();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}

// Never touches SteamVR or the user's Steam configuration: files live in a temp folder and live access is faked.
public sealed class SettingsViewModelTests : IDisposable
{
    private const string Defaults = """
        {"driver_vrlink":{"targetBandwidth":200,"recommendedBandwidthMbit":200,"encodeWidth":1536,
          "wirelessShutdownTimeout":30,"displayFrequency":90,"forceOpenMode":false,"10bit":true}}
        """;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SteamLinkVRSettings-vm-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _service;
    private readonly FakeLive _live = new();

    public SettingsViewModelTests()
    {
        Directory.CreateDirectory(_directory);
        _service = new SettingsService
        {
            UserConfigPath = Path.Combine(_directory, "steamvr.vrsettings"),
            DefaultsPath = Path.Combine(_directory, "default.vrsettings")
        };
        File.WriteAllText(_service.DefaultsPath, Defaults);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<SettingsViewModel> LoadAsync(string? userConfig)
    {
        if (userConfig is not null) await File.WriteAllTextAsync(_service.UserConfigPath, userConfig);
        var vm = new SettingsViewModel(_service, _live);
        await vm.LoadCommand.ExecuteAsync(null);
        return vm;
    }

    private static SettingItem Setting(SettingsViewModel vm, string key) => vm.Categories.SelectMany(c => c.Settings).Single(s => s.Key == key);
    private JsonNode Section() => JsonNode.Parse(File.ReadAllText(_service.UserConfigPath))!["driver_vrlink"]!;

    [Fact]
    public async Task OneUnreadableValueDoesNotBlockTheOtherSettings()
    {
        var vm = await LoadAsync("""{"driver_vrlink":{"encodeWidth":"wide","targetBandwidth":300}}""");

        Assert.False(vm.IsStatusError);
        Assert.Equal(300.0, Setting(vm, "targetBandwidth").CurrentValue);
        var broken = Setting(vm, "encodeWidth");
        Assert.Equal(1536, broken.CurrentValue);
        Assert.NotNull(broken.LoadWarning);
        Assert.Contains("Encode Width", vm.StatusMessage);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task MissingDefaultsFileStillLoadsUserValues()
    {
        File.Delete(_service.DefaultsPath);
        var vm = await LoadAsync("""{"driver_vrlink":{"targetBandwidth":250}}""");

        Assert.False(vm.IsStatusError);
        Assert.Equal(250.0, Setting(vm, "targetBandwidth").CurrentValue);
        Assert.Null(Setting(vm, "targetBandwidth").DefaultValue);
        Assert.Contains("default", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnlistedDropdownValueGetsACustomEntryInsteadOfTheFirstOption()
    {
        var vm = await LoadAsync("""{"driver_vrlink":{"wirelessShutdownTimeout":120}}""");
        var timeout = Setting(vm, "wirelessShutdownTimeout");

        Assert.Equal("Custom (120)", timeout.Options[timeout.SelectedIndex].Label);
        Assert.Equal(120, timeout.CurrentValue);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task TargetBandwidthIsAlwaysAFloatAndRecommendedStaysAnInteger()
    {
        _live.IsRunning = true;
        var vm = await LoadAsync(null);
        Setting(vm, "targetBandwidth").NumericValue = 300;
        Setting(vm, "recommendedBandwidthMbit").NumericValue = 300;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsStatusError, vm.StatusMessage);
        Assert.IsType<double>(_live.Applied["targetBandwidth"]);
        Assert.IsType<int>(_live.Applied["recommendedBandwidthMbit"]);
    }

    [Fact]
    public async Task ResettingToDefaultRemovesTheOverride()
    {
        var vm = await LoadAsync("""{"other":{"keep":true},"driver_vrlink":{"targetBandwidth":300,"futureKey":1}}""");
        Setting(vm, "targetBandwidth").ResetToDefaultCommand.Execute(null);
        Assert.True(vm.HasUnsavedChanges);
        await vm.SaveCommand.ExecuteAsync(null);

        var section = Section();
        Assert.Null(section["targetBandwidth"]);
        Assert.Equal(1, section["futureKey"]!.GetValue<int>());
        Assert.False(vm.HasUnsavedChanges);
        Assert.True(File.Exists(_service.UserConfigPath + SettingsService.OriginalBackupSuffix));
    }

    [Fact]
    public async Task ResetWhileSteamVrRunsUsesLiveRemoval()
    {
        _live.IsRunning = true;
        var vm = await LoadAsync("""{"driver_vrlink":{"targetBandwidth":300}}""");
        Setting(vm, "targetBandwidth").ResetToDefaultCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["targetBandwidth"], _live.Removed);
        Assert.Empty(_live.Applied);
    }

    [Fact]
    public async Task ExperimentalChangesAreReportedForConfirmation()
    {
        var vm = await LoadAsync(null);
        Assert.Empty(vm.ChangedExperimentalSettings);
        Setting(vm, "forceOpenMode").BoolValue = true;
        Assert.Equal(["forceOpenMode"], vm.ChangedExperimentalSettings.Select(s => s.Key));
    }

    [Fact]
    public async Task OriginalBackupIsKeptAcrossSaves()
    {
        const string original = """{"driver_vrlink":{"targetBandwidth":260}}""";
        var vm = await LoadAsync(original);
        Setting(vm, "targetBandwidth").NumericValue = 300;
        await vm.SaveCommand.ExecuteAsync(null);
        Setting(vm, "targetBandwidth").NumericValue = 350;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(original, File.ReadAllText(_service.UserConfigPath + SettingsService.OriginalBackupSuffix));
        Assert.Equal(350, Section()["targetBandwidth"]!.GetValue<double>());
    }

    private sealed class FakeLive : ILiveSettings
    {
        public bool IsRunning { get; set; }
        public Dictionary<string, object?> Applied { get; } = [];
        public List<string> Removed { get; } = [];

        public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, IReadOnlyCollection<string> removeKeys)
        {
            foreach (var pair in values) Applied[pair.Key] = pair.Value;
            Removed.AddRange(removeKeys);
            return Task.CompletedTask;
        }
    }
}
