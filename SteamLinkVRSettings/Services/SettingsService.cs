using System.Text.Json;
using System.Text.Json.Nodes;

namespace SteamLinkVRSettings.Services;

public class SettingsService
{
    private const string SectionName = "driver_vrlink";

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public string UserConfigPath { get; set; } =
        @"C:\Program Files (x86)\Steam\config\steamvr.vrsettings";

    public string DefaultsPath { get; set; } =
        @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\drivers\vrlink\resources\settings\default.vrsettings";

    public async Task<Dictionary<string, object?>> LoadDefaultsAsync()
    {
        var json = await ReadFileAsync(DefaultsPath);
        var node = JsonNode.Parse(json, documentOptions: DocOptions);
        var section = node?[SectionName]?.AsObject();
        return section is null ? [] : ExtractValues(section);
    }

    public async Task<Dictionary<string, object?>> LoadUserOverridesAsync()
    {
        if (!File.Exists(UserConfigPath))
            return [];

        var json = await ReadFileAsync(UserConfigPath);
        var node = JsonNode.Parse(json, documentOptions: DocOptions);
        var section = node?[SectionName]?.AsObject();
        return section is null ? [] : ExtractValues(section);
    }

    public async Task SaveUserOverridesAsync(Dictionary<string, object?> settings)
    {
        JsonNode root;
        if (File.Exists(UserConfigPath))
        {
            var json = await ReadFileAsync(UserConfigPath);
            root = JsonNode.Parse(json, documentOptions: DocOptions) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        var obj = root.AsObject();
        var section = obj[SectionName]?.AsObject();
        if (section is null)
        {
            section = new JsonObject();
            obj[SectionName] = section;
        }

        // Preserve unknown keys already in the section
        var existingKeys = section.Select(k => k.Key).ToHashSet();

        foreach (var kvp in settings)
        {
            section.Remove(kvp.Key);
            section.Add(kvp.Key, ToJsonNode(kvp.Value));
        }

        await WriteFileAsync(UserConfigPath, root.ToJsonString(WriteOptions));
    }

    public async Task<Dictionary<string, object?>> LoadMergedAsync()
    {
        var defaults = await LoadDefaultsAsync();
        var overrides = await LoadUserOverridesAsync();

        foreach (var kvp in overrides)
        {
            defaults[kvp.Key] = kvp.Value;
        }
        return defaults;
    }

    private static Dictionary<string, object?> ExtractValues(JsonObject section)
    {
        var result = new Dictionary<string, object?>();
        foreach (var kvp in section)
        {
            result[kvp.Key] = GetTypedValue(kvp.Value);
        }
        return result;
    }

    private static object? GetTypedValue(JsonNode? node)
    {
        if (node is null) return null;
        var element = node.GetValue<JsonElement>();
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt32(out var i) => i,
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.String => element.GetString(),
            _ => node.ToJsonString()
        };
    }

    private static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(value.ToString())
    };

    private static async Task<string> ReadFileAsync(string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return await reader.ReadToEndAsync();
    }

    private static async Task WriteFileAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, content);
    }
}
