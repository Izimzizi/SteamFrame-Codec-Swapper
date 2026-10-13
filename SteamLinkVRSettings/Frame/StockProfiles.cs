using System.Text.Json;
using SteamLinkVRSettings.Services;

namespace SteamLinkVRSettings.Frame;

public sealed record TuningProfile
{
    public int Version { get; init; } = 1;
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, JsonElement> Driver { get; init; } = [];
    public Dictionary<string, JsonElement> Video { get; init; } = [];
    public bool BitratePatch { get; init; }
    public StreamCodec Codec { get; init; }
    public int TargetMbps { get; init; } = 100;
    public int Fps { get; init; } = 90;
    public bool QualityEnabled { get; init; }
    public int GazeOffset { get; init; } = -4;
    public int PeripheralOffset { get; init; } = 8;
    public bool Reapply { get; init; }
    public EncoderRateControl RateControl { get; init; }
    public EncoderMultiPass MultiPass { get; init; }
    public AdvancedEncoding Advanced { get; init; } = new();
    public bool IsStock => Driver.Count == 0 && Video.Count == 0 && !BitratePatch && Codec == StreamCodec.Hevc && !QualityEnabled && !Reapply && RateControl == EncoderRateControl.ValveDefault && MultiPass == EncoderMultiPass.ValveDefault && Advanced.IsDefault;
}

/// <summary>Only tuning values are backed up; pairing and other SteamVR sections stay intact.</summary>
public sealed class StockProfiles(SettingsService driver, SettingsService video, string snapshotPath)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamLinkVRSettings", "previous-tuning.json");

    public async Task<TuningProfile> CaptureAsync(FramePreferences preferences, bool patched, CodecServiceStatus? session = null)
    {
        var driverValues = await driver.LoadUserOverridesAsync();
        var videoValues = await video.LoadUserOverridesAsync();
        return new()
        {
            Driver = Serialize(driverValues, SettingOwnership.TuningKeys), Video = Serialize(videoValues, SettingOwnership.VideoKeys),
            BitratePatch = patched, Codec = session?.Live == true ? StreamCodecStatus.Normalize(session.ActiveCodec) switch { "AV1" => StreamCodec.Av1, "H.264" => StreamCodec.H264, _ => StreamCodec.Hevc } : preferences.RequestedCodec,
            TargetMbps = session?.Live == true ? session.TargetMbps : preferences.RequestedCodec == StreamCodec.Hevc && driverValues.TryGetValue("targetBandwidth", out var target) ? Math.Clamp((int)Convert.ToDouble(target), 25, 500) : preferences.CodecTargetMbps, Fps = session?.Live == true ? session.Fps : preferences.CodecFps,
            QualityEnabled = preferences.QualityEnabled, GazeOffset = preferences.GazeOffset, PeripheralOffset = preferences.PeripheralOffset,
            Reapply = preferences.ReapplyAfterRestart, RateControl = preferences.RateControl, MultiPass = preferences.MultiPass, Advanced = preferences.AdvancedEncoding
        };
    }

    public async Task SaveAsync(TuningProfile profile)
    {
        Validate(profile);
        var destination = Path.GetFullPath(snapshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<TuningProfile?> ReadAsync()
    {
        if (!File.Exists(snapshotPath)) return null;
        if (new FileInfo(snapshotPath).Length > 1024 * 1024) throw new IOException("The saved tuning profile is too large.");
        var profile = WithoutUnrelated(JsonSerializer.Deserialize<TuningProfile>(await File.ReadAllTextAsync(snapshotPath)) ?? throw new IOException("The tuning profile is empty."));
        Validate(profile); return profile;
    }

    /// <summary>Older presets and snapshots also stored non-tuning settings. Ignore them rather than reapplying them.</summary>
    public static TuningProfile WithoutUnrelated(TuningProfile profile) => profile?.Driver is null || !profile.Driver.Keys.Any(SettingOwnership.UnrelatedKeys.Contains) ? profile!
        : profile with { Driver = profile.Driver.Where(pair => !SettingOwnership.UnrelatedKeys.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value) };

    public async Task ResetAsync(FramePreferences preferences)
    {
        await driver.SaveUserOverridesAsync([], SettingOwnership.TuningKeys);
        await video.SaveUserOverridesAsync([], SettingOwnership.VideoKeys);
        ApplyPreferences(preferences, new());
    }

    public async Task RestoreAsync(TuningProfile profile, FramePreferences preferences)
    {
        Validate(profile);
        await driver.SaveUserOverridesAsync(Deserialize(profile.Driver), SettingOwnership.TuningKeys.Except(profile.Driver.Keys));
        await video.SaveUserOverridesAsync(Deserialize(profile.Video), SettingOwnership.VideoKeys.Except(profile.Video.Keys));
        ApplyPreferences(preferences, profile);
    }

    private static void ApplyPreferences(FramePreferences preferences, TuningProfile profile)
    {
        preferences.RequestedCodec = profile.Codec; preferences.CodecTargetMbps = profile.TargetMbps; preferences.CodecFps = profile.Fps;
        preferences.QualityEnabled = profile.QualityEnabled; preferences.GazeOffset = profile.GazeOffset; preferences.PeripheralOffset = profile.PeripheralOffset;
        preferences.ReapplyAfterRestart = profile.Reapply; preferences.RateControl = profile.RateControl; preferences.MultiPass = profile.MultiPass; preferences.AdvancedEncoding = profile.Advanced;
    }
    private static Dictionary<string, JsonElement> Serialize(Dictionary<string, object?> values, IEnumerable<string> keys)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var key in keys.Where(values.ContainsKey))
        {
            var element = JsonSerializer.SerializeToElement(values[key]);
            // Overrides outside the app's supported ranges, such as a target saved by an older
            // release, are left out instead of blocking Back to stock or Save preset.
            try { foreach (var value in Deserialize(new() { [key] = element })) SettingOwnership.ValidateValue(value.Key, value.Value); }
            catch (Exception error) when (error is IOException or ArgumentException or OverflowException or InvalidOperationException) { continue; }
            result[key] = element;
        }
        return result;
    }
    public static Dictionary<string, object?> Deserialize(Dictionary<string, JsonElement> values) => values.ToDictionary(pair => pair.Key, pair => pair.Value.ValueKind switch
    {
        JsonValueKind.True => (object?)true, JsonValueKind.False => false, JsonValueKind.Number => SettingOwnership.NormalizeValue(pair.Key, pair.Value.GetDouble()), JsonValueKind.String => pair.Value.GetString(), _ => throw new IOException("The profile contains a nonscalar setting.")
    });
    public static void Validate(TuningProfile profile)
    {
        if (profile is null || profile.Driver is null || profile.Video is null || profile.Advanced is null) throw new IOException("The saved tuning profile is incomplete.");
        if (profile.Version != 1 || !Enum.IsDefined(profile.Codec) || !Enum.IsDefined(profile.RateControl) || !Enum.IsDefined(profile.MultiPass) || profile.TargetMbps < 25 || profile.TargetMbps > CodecSwitchRequest.MaximumMbpsFor(profile.Codec) || profile.Fps is not (90 or 120)
            || profile.Driver.Keys.Any(key => !SettingOwnership.TuningKeys.Contains(key)) || profile.Video.Keys.Any(key => !SettingOwnership.VideoKeys.Contains(key)))
            throw new IOException("The saved tuning profile is incompatible.");
        profile.Advanced.Validate();
        foreach (var value in Deserialize(profile.Driver).Concat(Deserialize(profile.Video))) SettingOwnership.ValidateValue(value.Key, value.Value);
        if (!FoveationOffsets.IsValid(profile.GazeOffset, FoveationOffsets.MaximumGaze, FoveationOffsets.MinimumGaze) || !FoveationOffsets.IsValid(profile.PeripheralOffset, FoveationOffsets.MaximumPeripheral))
            throw new IOException("The profile contains unsupported gaze-quality offsets.");
    }
}
