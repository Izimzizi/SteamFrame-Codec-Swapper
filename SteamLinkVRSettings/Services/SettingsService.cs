using System.Text.Json;
using System.Text.Json.Nodes;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Services;

public class SettingsService
{
    private const string SectionName = "driver_vrlink";
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public const string BackupSuffix = ".SteamLinkVRSettings.bak";
    public const string OriginalBackupSuffix = ".SteamLinkVRSettings.original.bak";

    public string UserConfigPath { get; set; } = SteamPaths.Current.Config;

    public string DefaultsPath { get; set; } = SteamPaths.Current.Defaults;
    public string Section { get; init; } = SectionName;

    /// <summary>Valve's defaults. A missing defaults file (SteamVR moved or not installed) yields no defaults rather than an error.</summary>
    public async Task<Dictionary<string, object?>> LoadDefaultsAsync()
    {
        if (!File.Exists(DefaultsPath))
            return [];
        var json = await ReadFileAsync(DefaultsPath);
        var node = JsonNode.Parse(json, documentOptions: DocOptions);
        var section = node?[Section]?.AsObject();
        return section is null ? [] : ExtractValues(section);
    }

    public async Task<Dictionary<string, object?>> LoadUserOverridesAsync()
    {
        if (!File.Exists(UserConfigPath))
            return [];

        var json = await ReadFileAsync(UserConfigPath);
        var node = JsonNode.Parse(json, documentOptions: DocOptions);
        var section = node?[Section]?.AsObject();
        return section is null ? [] : ExtractValues(section);
    }

    /// <summary>
    /// Writes <paramref name="settings"/> into the driver section and removes <paramref name="removeKeys"/>,
    /// so removed settings fall back to Valve's current defaults.
    /// </summary>
    public async Task SaveUserOverridesAsync(Dictionary<string, object?> settings, IEnumerable<string>? removeKeys = null)
    {
        await SaveGate.WaitAsync();
        try { await SaveCoreAsync(settings, removeKeys?.ToArray() ?? []); }
        finally { SaveGate.Release(); }
    }

    private async Task SaveCoreAsync(Dictionary<string, object?> settings, string[] removeKeys)
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
        var section = obj[Section]?.AsObject();
        if (section is null)
        {
            section = new JsonObject();
            obj[Section] = section;
        }

        foreach (var kvp in settings)
        {
            section.Remove(kvp.Key);
            section.Add(kvp.Key, ToJsonNode(kvp.Value));
        }
        foreach (var key in removeKeys)
        {
            if (!settings.ContainsKey(key)) section.Remove(key);
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
        if (node is not JsonValue) return node.DeepClone();
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
        JsonNode node => node.DeepClone(),
        _ => JsonValue.Create(value.ToString())
    };

    /// <summary>SteamVR rewrites its settings file exclusively for a moment after every change, so a read that lands then is retried briefly.</summary>
    private const int ReadAttempts = 8;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(150);

    private static async Task<string> ReadFileAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                return await reader.ReadToEndAsync();
            }
            catch (IOException error) when (attempt < ReadAttempts && IsSharingViolation(error))
            {
                await Task.Delay(ReadRetryDelay);
            }
        }
    }

    private static bool IsSharingViolation(IOException error)
        => error is not FileNotFoundException and not DirectoryNotFoundException
           && (error.HResult & 0xFFFF) is 32 or 33; // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION

    private static async Task WriteFileAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content);
            // Keep the user's pre-app configuration once, independent of the rolling previous-revision backup.
            var original = path + OriginalBackupSuffix;
            if (File.Exists(path) && !File.Exists(original)) File.Copy(path, original);
            if (File.Exists(path)) File.Replace(temporary, path, path + BackupSuffix);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
