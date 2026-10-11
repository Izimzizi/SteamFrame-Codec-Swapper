using System.Diagnostics;
using System.Reflection;
using SteamLinkVRSettings.Frame;

if (args.Length != 1) throw new ArgumentException("Pass the built native helper path.");
var readExports = typeof(EncoderProbe).GetMethod("ReadExports", BindingFlags.NonPublic | BindingFlags.Static)!;
var exports = (Dictionary<string, uint>)readExports.Invoke(null, [Path.GetFullPath(args[0])])!;
if (exports.Count < 5 || !exports.ContainsKey("WriteStatus")) throw new Exception("Missing native exports.");
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
var snapshot = await probe.GetSnapshotAsync();
var after = Process.GetProcessesByName("vrserver").Select(p => p.Id).Order().ToArray();
if (!before.SequenceEqual(after) || snapshot.Installed) throw new Exception("Idle polling started monitoring or SteamVR.");
if (before.Length == 0)
{
    var start = await probe.StartAsync();
    if (start.ProcessId != 0 || start.Installed || Process.GetProcessesByName("vrserver").Length != 0)
        throw new Exception("Absent-server Start launched SteamVR.");
}
Console.WriteLine("PASS: x64 export parser, invalid-image guard, quality ranges, read-only idle polling. " +
    (before.Length == 0 ? "Absent-server Start passed." : "Absent-server Start skipped because SteamVR is running."));
