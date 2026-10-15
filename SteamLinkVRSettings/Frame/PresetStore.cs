using System.Text.Json;

namespace SteamLinkVRSettings.Frame;

public sealed record NamedPreset(string Id, string Name, TuningProfile Profile)
{
    [System.Text.Json.Serialization.JsonIgnore] public bool IsBuiltIn { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public string? Description { get; init; }
}
public sealed class PresetStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamLinkVRSettings", "presets.json");
    public async Task<IReadOnlyList<NamedPreset>> ReadAsync()
    {
        if (!File.Exists(_path)) return [];
        if (new FileInfo(_path).Length > 4 * 1024 * 1024) throw new IOException("The preset file is too large.");
        var values = (JsonSerializer.Deserialize<List<NamedPreset>>(await File.ReadAllTextAsync(_path)) ?? throw new IOException("The preset file is empty."))
            .Select(value => value is null ? throw new IOException("The preset catalog is incompatible.") : value with { Profile = StockProfiles.WithoutUnrelated(value.Profile) }).ToList();
        if (values.Count > 200 || values.Select(x => x.Id).Distinct().Count() != values.Count) throw new IOException("The preset catalog is incompatible.");
        foreach (var value in values) { ValidateName(value.Name); if (!Guid.TryParseExact(value.Id, "D", out _)) throw new IOException("A preset identifier is invalid."); StockProfiles.Validate(value.Profile); }
        return values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public async Task SaveAsync(string name, TuningProfile profile)
    {
        name = name.Trim(); ValidateName(name); StockProfiles.Validate(profile);
        var values = (await ReadAsync()).ToList();
        var existing = values.FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var preset = new NamedPreset(existing >= 0 ? values[existing].Id : Guid.NewGuid().ToString("D"), name, profile);
        if (existing >= 0) values[existing] = preset; else values.Add(preset);
        if (values.Count > 200) throw new IOException("The catalog already contains 200 presets.");
        await WriteAsync(values);
    }
    /// <summary>Removes every preset the predicate selects and returns how many went.</summary>
    public async Task<int> RemoveAsync(Func<NamedPreset, bool> predicate)
    {
        var values = (await ReadAsync()).ToList();
        var removed = values.RemoveAll(value => predicate(value));
        if (removed > 0) await WriteAsync(values);
        return removed;
    }
    private async Task WriteAsync(List<NamedPreset> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("Use a preset name from 1 to 80 characters, without line breaks.");
    }
}
