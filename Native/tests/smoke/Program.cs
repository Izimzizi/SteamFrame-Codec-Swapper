using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32.SafeHandles;
using SteamFrame.Core;

if (args.Length == 1 && args[0] == "--loader-baseline")
{
    // Development diagnostic: add and immediately balance one reference to the
    // system crypto DLL already present in SteamVR. No probe or hooks are started.
    using var process = Process.GetProcessesByName("vrserver").Single();
    var type = typeof(EncoderProbe);
    object? Call(string name, params object?[] values) => type.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
        .Single(m => m.Name == name && m.GetParameters().Length == values.Length &&
            m.GetParameters().Select((p, i) => values[i] is null || p.ParameterType.IsInstanceOfType(values[i])).All(x => x))
        .Invoke(null, values);
    var modules = Call("ReadModules", process)!;
    using var handle = (SafeProcessHandle)Call("OpenTarget", process.Id)!;
    var existing = process.Modules.Cast<ProcessModule>().Single(m => m.ModuleName.Equals("bcrypt.dll", StringComparison.OrdinalIgnoreCase));
    var helperHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Native", "SteamFrameEncoderProbe.dll")))).ToLowerInvariant();
    var helperPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamLinkVRSettings", "EncoderProbe", helperHash, "SteamFrameEncoderProbe.dll");
    var attributes = (nint)Call("ResolveRemoteSystemExport", modules, "kernel32.dll", "GetFileAttributesW")!;
    foreach (var checkPath in new[] { helperPath, Path.Combine(AppContext.BaseDirectory, "Native", "SteamFrameEncoderProbe.dll"), existing.FileName, Path.GetDirectoryName(helperPath)! })
    {
        var remoteAttributes = (uint)Call("Invoke", handle, attributes, System.Text.Encoding.Unicode.GetBytes(checkPath + "\0"))!;
        Console.WriteLine(JsonSerializer.Serialize(new { Event = "remote-helper-attributes", Path = checkPath, LocalFound = File.Exists(checkPath) || Directory.Exists(checkPath), Found = remoteAttributes != uint.MaxValue, Attributes = remoteAttributes }));
    }
    var loaded = (nint)Call("LoadRemoteHelper", handle, modules, existing.FileName)!;
    Console.WriteLine(JsonSerializer.Serialize(new { Event = "loader-baseline", MatchesExisting = loaded == existing.BaseAddress, Path = existing.FileName }));
    var free = (nint)Call("ResolveRemoteSystemExport", modules, "kernel32.dll", "FreeLibrary")!;
    var result = (uint)Call("Invoke", handle, free, loaded, null)!;
    Console.WriteLine(JsonSerializer.Serialize(new { Event = "balanced-free", Success = result != 0 }));
    return result != 0 && loaded == existing.BaseAddress ? 0 : 4;
}
// A normal launch is inert. --run is required for the coordinated live test.
if (args.Length != 1 || args[0] != "--run")
{
    Console.WriteLine("Use --run only when a live encoder-monitor smoke test has been authorized. No monitoring started.");
    return 2;
}
var probe = new EncoderProbe();
var rates = new RateTracker();
var started = false;
try
{
    var first = await probe.StartAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { Event = "start", Snapshot = first }));
    started = first.Installed;
    if (!started) return 3;
    if (first.Active) rates.Sample(first.Generation, first.EncodedBytes, first.EncodedFrames, first.TimestampSeconds);
    for (var sample = 1; sample <= 8; ++sample)
    {
        await Task.Delay(1000);
        var snapshot = await probe.GetSnapshotAsync();
        (double? Mbps, double? Fps) rate = (null, null);
        if (snapshot.Installed && snapshot.Active)
            rate = rates.Sample(snapshot.Generation, snapshot.EncodedBytes, snapshot.EncodedFrames, snapshot.TimestampSeconds);
        else rates.Reset();
        Console.WriteLine(JsonSerializer.Serialize(new { Event = "sample", Sample = sample, Snapshot = snapshot, ActualVideoMbps = rate.Mbps, EncodedFps = rate.Fps }));
    }
    return 0;
}
finally
{
    if (started)
    {
        var stopped = await probe.StopAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { Event = "stop", Snapshot = stopped }));
    }
}
