using System.Diagnostics;
using System.Text.Json;
using SteamFrame.Core;

namespace CodecSwapper;

public enum SteamVrDriverState { NotInstalled, Installed, Outdated }

/// <summary>
/// The optional SteamVR driver: the hook DLL registered once as a SteamVR external driver, so every SteamVR start
/// has it resident (streaming stock HEVC until something switches it). Registration goes through SteamVR's own vrpathreg.
/// </summary>
public sealed class SteamVrDriver(SteamPaths steam, string packageDirectory, string installDirectory, string pathsFile, Func<string[], (int Code, string Output)>? run = null)
{
    private readonly Func<string[], (int Code, string Output)> _run = run ?? (arguments => RunVrPathReg(steam, arguments));

    public static SteamVrDriver ForThisPc(SteamPaths steam) => new(steam, Path.Combine(AppContext.BaseDirectory, "CodecLab", "Backend", "Driver"), Path.Combine(AppData.Root, "Driver"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath"));

    private string InstalledDll => Path.Combine(installDirectory, "bin", "win64", "driver_steamframe.dll");

    public bool PackageAvailable => File.Exists(Path.Combine(packageDirectory, "bin", "win64", "driver_steamframe.dll"));

    /// <summary>Whether SteamVR's driver list names our folder, and whether the installed copy matches this tray's.</summary>
    public SteamVrDriverState Inspect()
    {
        if (!IsRegistered()) return SteamVrDriverState.NotInstalled;
        var package = Path.Combine(packageDirectory, "bin", "win64", "driver_steamframe.dll");
        if (!File.Exists(InstalledDll)) return SteamVrDriverState.Outdated;
        return File.Exists(package) && !FilesEqual(package, InstalledDll) ? SteamVrDriverState.Outdated : SteamVrDriverState.Installed;
    }

    public bool IsRegistered()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(pathsFile));
            if (!document.RootElement.TryGetProperty("external_drivers", out var drivers) || drivers.ValueKind != JsonValueKind.Array) return false;
            var wanted = Path.GetFullPath(installDirectory).TrimEnd('\\');
            return drivers.EnumerateArray().Any(d => d.ValueKind == JsonValueKind.String && string.Equals(Path.GetFullPath(d.GetString() ?? "").TrimEnd('\\'), wanted, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>Copies the driver into the tray's data folder and registers it. SteamVR must be closed: it holds the DLL while it runs.</summary>
    public void Install()
    {
        if (!PackageAvailable) throw new InvalidOperationException("This tray build does not include the driver files.");
        try { CopyPackage(); }
        catch (IOException) { throw new InvalidOperationException("The driver file is in use. Close SteamVR and try again."); }
        if (!IsRegistered())
        {
            var (code, output) = _run(["adddriver", installDirectory]);
            if (code != 0 || !IsRegistered()) throw new InvalidOperationException("SteamVR's driver registration failed: " + output.Trim());
        }
    }

    public void Remove()
    {
        if (IsRegistered())
        {
            var (code, output) = _run(["removedriver", installDirectory]);
            if (code != 0 || IsRegistered()) throw new InvalidOperationException("Removing the driver from SteamVR failed: " + output.Trim());
        }
        try { if (Directory.Exists(installDirectory)) Directory.Delete(installDirectory, true); }
        catch (IOException) { /* still loaded by a running SteamVR; it is unregistered, so the next start does not load it */ }
    }

    private void CopyPackage()
    {
        foreach (var file in Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(installDirectory, Path.GetRelativePath(packageDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static bool FilesEqual(string a, string b)
    {
        var first = new FileInfo(a);var second = new FileInfo(b);
        return first.Length == second.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    private static (int Code, string Output) RunVrPathReg(SteamPaths steam, string[] arguments)
    {
        var tool = Path.Combine(steam.SteamVrRoot, "bin", "win64", "vrpathreg.exe");
        if (!File.Exists(tool)) throw new InvalidOperationException("SteamVR's vrpathreg.exe was not found; is SteamVR installed?");
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("vrpathreg did not start.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15000)) { try { process.Kill(); } catch (InvalidOperationException) { } throw new InvalidOperationException("vrpathreg did not finish."); }
        return (process.ExitCode, output);
    }
}
