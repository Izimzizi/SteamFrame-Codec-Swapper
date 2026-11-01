using System.Text.Json;
using SteamFrame.Core;
using CodecSwapper;

namespace CodecSwapperTests;

public sealed class SteamVrDriverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "driver-" + Guid.NewGuid().ToString("N"));
    private readonly List<string[]> _calls = [];
    private readonly SteamVrDriver _driver;
    private string Package => Path.Combine(_root, "package");
    private string Installed => Path.Combine(_root, "installed");
    private string Paths => Path.Combine(_root, "openvrpaths.vrpath");

    public SteamVrDriverTests()
    {
        Directory.CreateDirectory(Path.Combine(Package, "bin", "win64"));
        File.WriteAllText(Path.Combine(Package, "bin", "win64", "driver_steamframe.dll"), "v1");
        File.WriteAllText(Path.Combine(Package, "driver.vrdrivermanifest"), "{}");
        File.WriteAllText(Paths, "{\"external_drivers\":null}");
        _driver = new SteamVrDriver(new SteamPaths(_root, _root), Package, Installed, Paths, Run);
    }

    // Behaves like vrpathreg: edits the driver list in the paths file.
    private (int, string) Run(string[] arguments)
    {
        _calls.Add(arguments);
        var drivers = new List<string>();
        using (var document = JsonDocument.Parse(File.ReadAllText(Paths)))
            if (document.RootElement.TryGetProperty("external_drivers", out var list) && list.ValueKind == JsonValueKind.Array)
                drivers.AddRange(list.EnumerateArray().Select(d => d.GetString()!));
        if (arguments[0] == "adddriver") drivers.Add(arguments[1]); else drivers.Remove(arguments[1]);
        File.WriteAllText(Paths, JsonSerializer.Serialize(new { external_drivers = drivers }));
        return (0, "");
    }

    [Fact]
    public void Installing_copies_the_package_and_registers_it_once()
    {
        Assert.Equal(SteamVrDriverState.NotInstalled, _driver.Inspect());
        _driver.Install();
        _driver.Install();
        Assert.True(File.Exists(Path.Combine(Installed, "bin", "win64", "driver_steamframe.dll")));
        Assert.True(File.Exists(Path.Combine(Installed, "driver.vrdrivermanifest")));
        Assert.Single(_calls);
        Assert.Equal(SteamVrDriverState.Installed, _driver.Inspect());
    }

    [Fact]
    public void A_different_installed_copy_is_reported_as_outdated_and_updated_by_installing()
    {
        _driver.Install();
        File.WriteAllText(Path.Combine(Package, "bin", "win64", "driver_steamframe.dll"), "v2");
        Assert.Equal(SteamVrDriverState.Outdated, _driver.Inspect());
        _driver.Install();
        Assert.Equal(SteamVrDriverState.Installed, _driver.Inspect());
        Assert.Equal("v2", File.ReadAllText(Path.Combine(Installed, "bin", "win64", "driver_steamframe.dll")));
    }

    [Fact]
    public void Removing_unregisters_and_deletes_the_copy()
    {
        _driver.Install();
        _driver.Remove();
        Assert.Equal(SteamVrDriverState.NotInstalled, _driver.Inspect());
        Assert.False(Directory.Exists(Installed));
        Assert.Equal("removedriver", _calls[^1][0]);
    }

    [Fact]
    public void Other_registered_drivers_are_not_mistaken_for_ours()
    {
        File.WriteAllText(Paths, JsonSerializer.Serialize(new { external_drivers = new[] { Path.Combine(_root, "someone-else") } }));
        Assert.Equal(SteamVrDriverState.NotInstalled, _driver.Inspect());
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
}
