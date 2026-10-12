using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SteamLinkVRSettings.Frame;

if (args.Length != 1) throw new ArgumentException("Pass the built native helper path.");
var readExports = typeof(EncoderProbe).GetMethod("ReadExports", BindingFlags.NonPublic | BindingFlags.Static)!;
var exports = (Dictionary<string, uint>)readExports.Invoke(null, [Path.GetFullPath(args[0])])!;
if (exports.Count < 5 || !exports.ContainsKey("WriteStatus")) throw new Exception("Missing native exports.");
// Produce metadata in this isolated test process, without StartProbe/NVENC/SteamVR,
// then verify the optional advanced/timing JSON contract is readable by the app.
if (exports.ContainsKey("SetAdvancedEncoding"))
{
    var statusPath = Path.Combine(Path.GetTempPath(), $"frame-probe-schema-{Guid.NewGuid():N}.json");
    var library = NativeLibrary.Load(Path.GetFullPath(args[0]));
    try
    {
        var write = Marshal.GetDelegateForFunctionPointer<WriteStatusExport>(NativeLibrary.GetExport(library, "WriteStatus"));
        if (write(statusPath) != 0) throw new Exception("Local metadata snapshot failed.");
        using var document = JsonDocument.Parse(File.ReadAllText(statusPath));
        var advanced = (AdvancedEncodingSnapshot?)typeof(EncoderProbe).GetMethod("ReadAdvancedEncoding", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [document.RootElement]);
        var timing = (EncoderCallbackTiming?)typeof(EncoderProbe).GetMethod("ReadCallbackTiming", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [document.RootElement]);
        if (advanced is not { Supported: true } || !advanced.Requested.IsDefault || advanced.AppliedFlags != 0 || timing is null || timing.EncodeCalls != 0)
            throw new Exception("Advanced native/managed metadata contract mismatch.");
    }
    finally { NativeLibrary.Free(library); if (File.Exists(statusPath)) File.Delete(statusPath); }
}
var malformed = Path.Combine(Path.GetTempPath(), $"encoder-probe-invalid-{Guid.NewGuid():N}.dll");
try
{
    File.WriteAllBytes(malformed, new byte[256]);
    try { readExports.Invoke(null, [malformed]); throw new Exception("Invalid helper accepted."); }
    catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { }
}
finally { File.Delete(malformed); }
var probe = new EncoderProbe();
foreach (var offsets in new[] { (-21, 0), (13, 0), (0, -13), (0, 32) })
{
    try { _ = probe.SetQualityAsync(true, offsets.Item1, offsets.Item2); throw new Exception("Invalid offset accepted."); }
    catch (ArgumentOutOfRangeException) { }
}
foreach (var offsets in new[] { (0, 13), (0, 24), (-20, 31), (12, -12) })
{
    var pending = await probe.SetQualityAsync(true, offsets.Item1, offsets.Item2);
    if (pending.Installed || pending.Enabled) throw new Exception("Valid offset started a probe without an explicit Start.");
}
var before = Process.GetProcessesByName("vrserver").Select(p => p.Id).Order().ToArray();
var helperBefore = Process.GetProcessesByName("vrserver").Any(p => p.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("SteamFrameEncoderProbe.dll", StringComparison.OrdinalIgnoreCase)));
var snapshot = await probe.GetSnapshotAsync();
var after = Process.GetProcessesByName("vrserver").Select(p => p.Id).Order().ToArray();
var helperAfter = Process.GetProcessesByName("vrserver").Any(p => p.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("SteamFrameEncoderProbe.dll", StringComparison.OrdinalIgnoreCase)));
if (!before.SequenceEqual(after) || helperBefore != helperAfter || snapshot.Installed && !helperBefore) throw new Exception("Idle polling started monitoring or SteamVR.");
if (before.Length == 0)
{
    var start = await probe.StartAsync();
    if (start.ProcessId != 0 || start.Installed || Process.GetProcessesByName("vrserver").Length != 0)
        throw new Exception("Absent-server Start launched SteamVR.");
}
Console.WriteLine("PASS: x64 export parser, invalid-image guard, quality ranges, read-only idle polling. " +
    (before.Length == 0 ? "Absent-server Start passed." : "Absent-server Start skipped because SteamVR is running."));

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
delegate uint WriteStatusExport([MarshalAs(UnmanagedType.LPWStr)] string path);
