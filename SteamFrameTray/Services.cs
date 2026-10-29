using System.Text.Json;
using System.Text.Json.Nodes;
using SteamFrame.Core;

namespace SteamFrameTray;

/// <summary>The paired codec supervisor, as the tray uses it (implemented by <see cref="CodecController"/>).</summary>
public interface ICodecService
{
    string SessionDirectory { get; }
    Task<CodecServiceStatus> ReadStatusAsync(CancellationToken cancellationToken = default);
    Task<CodecServiceStatus> StartAsync(CodecStartRequest request, FramePreferences preferences, string? password,
        IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken, bool resume);
    Task RestoreLeftoversAsync(FramePreferences preferences, string? password, CancellationToken cancellationToken);
    /// <summary>Asks a live session to close without waiting (Windows is ending the session).</summary>
    void RequestCloseNoWait();
    Task<CodecServiceStatus> CloseAsync(IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken);
    Task<CodecServiceStatus> SetRateAsync(int targetMbps, IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken);
}

/// <summary>SteamVR's own live bitrate setting, which it applies to the encoder without a restart.</summary>
public interface ILiveBitrate
{
    bool SteamVrRunning { get; }
    Task ApplyAsync(int targetMbps);
    /// <summary>SteamVR's current target in Mbps, or null when it cannot be read.</summary>
    Task<double?> ReadTargetAsync();
    /// <summary>Writes the target into steamvr.vrsettings; only valid while SteamVR is closed.</summary>
    Task WriteFileAsync(int targetMbps);
}

/// <summary>Where the Frame user password lives (Windows Credential Manager).</summary>
public interface ICredentialVault
{
    string? Load();
    void Save(string password);
    void Delete();
}

/// <summary>The optional driver patch that raises SteamVR's bitrate ceiling to 500 Mbps.</summary>
public interface IDriverPatch
{
    Task<BitratePatchStatus> InspectAsync();
    Task<BitratePatchStatus> ApplyAsync();
    Task<BitratePatchStatus> RestoreAsync();
}

public sealed class CodecServiceAdapter(CodecController controller) : ICodecService
{
    public string SessionDirectory => controller.SessionDirectory;
    public Task<CodecServiceStatus> ReadStatusAsync(CancellationToken cancellationToken = default) => controller.ReadStatusAsync(cancellationToken);
    public Task<CodecServiceStatus> StartAsync(CodecStartRequest request, FramePreferences preferences, string? password,
        IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken, bool resume) =>
        controller.StartAsync(request, preferences, password, progress, cancellationToken, resume);
    public Task RestoreLeftoversAsync(FramePreferences preferences, string? password, CancellationToken cancellationToken) =>
        controller.RestoreLeftoversAsync(preferences, password, cancellationToken);
    public void RequestCloseNoWait() => controller.RequestCloseNoWait();
    public Task<CodecServiceStatus> CloseAsync(IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken) =>
        controller.CloseAsync(progress, cancellationToken);
    public Task<CodecServiceStatus> SetRateAsync(int targetMbps, IProgress<CodecServiceStatus>? progress, CancellationToken cancellationToken) =>
        controller.SetRateAsync(targetMbps, progress, cancellationToken);
}

public sealed class LiveBitrate(SteamPaths paths) : ILiveBitrate
{
    public bool SteamVrRunning => OpenVrSettings.IsRunning;

    public Task ApplyAsync(int targetMbps) => Task.Run(() =>
    {
        OpenVrSettings.Apply(paths, new Dictionary<string, object?> { ["targetBandwidth"] = (double)targetMbps, ["recommendedBandwidthMbit"] = targetMbps });
        // Asks Valve to reconfigure the encoder now (a brief nudge of the target, then the new value again).
        OpenVrSettings.RefreshEncoder(paths);
    });

    /// <summary>
    /// SteamVR's slider value, read from its settings file. No connection to SteamVR is kept open, so the tray
    /// is never attached to it when it exits.
    /// </summary>
    public Task<double?> ReadTargetAsync() => Task.Run<double?>(() => ReadFileTarget(paths.Config));

    public Task WriteFileAsync(int targetMbps) => Task.Run(() =>
    {
        if (OpenVrSettings.IsRunning) throw new InvalidOperationException("SteamVR is running; change the bitrate through it instead.");
        WriteFileTarget(paths.Config, targetMbps);
    });

    public static double? ReadFileTarget(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.TryGetProperty("driver_vrlink", out var section) && section.ValueKind == JsonValueKind.Object &&
                   section.TryGetProperty("targetBandwidth", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Sets the streaming target in SteamVR's settings file, keeping every other key. Only for use while SteamVR is closed.</summary>
    public static void WriteFileTarget(string path, int targetMbps)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            try { root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?.AsObject() ?? throw new JsonException(); }
            catch (JsonException error) { throw new IOException("SteamVR's settings file could not be read, so it was left unchanged.", error); }
        }
        else root = new JsonObject();
        if (root["driver_vrlink"] is not JsonObject section) root["driver_vrlink"] = section = new JsonObject();
        section["targetBandwidth"] = (double)targetMbps;
        section["recommendedBandwidthMbit"] = targetMbps;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tray-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class CredentialVault(FrameCredentialStore store) : ICredentialVault
{
    public string? Load() => store.Load();
    public void Save(string password) => store.Save(password);
    public void Delete() => store.Delete();
}

public sealed class DriverPatch(BitratePatcher patcher, string driverPath) : IDriverPatch
{
    public Task<BitratePatchStatus> InspectAsync() => patcher.InspectAsync(driverPath);
    public Task<BitratePatchStatus> ApplyAsync() => patcher.ApplyAsync(driverPath);
    public Task<BitratePatchStatus> RestoreAsync() => patcher.RestoreAsync(driverPath);
}
