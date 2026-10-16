using System.Diagnostics;
using SteamLinkVRSettings.Frame;

public class RuntimeProcessStateTests
{
    [Fact]
    public void LiveRuntimeStillBlocksDriverReplacement()
    {
        using var current = Process.GetCurrentProcess();
        Assert.True(RuntimeProcessState.AnyRunning([current]));
    }

    [Fact]
    public async Task ExitedRuntimeWithAnOpenHandleDoesNotBlockRestoration()
    {
        using var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        { UseShellExecute = false, CreateNoWindow = true })!;
        await exited.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.False(RuntimeProcessState.AnyRunning([exited]));
    }

    [Fact]
    public async Task ExitedRuntimeCannotHideAnotherLiveRuntime()
    {
        using var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        { UseShellExecute = false, CreateNoWindow = true })!;
        await exited.WaitForExitAsync(TestContext.Current.CancellationToken);
        using var current = Process.GetCurrentProcess();
        Assert.True(RuntimeProcessState.AnyRunning([exited, current]));
    }

    [Fact]
    public void UnknownRuntimeStateStillBlocksDriverReplacement()
    {
        using var unknown = new Process();
        Assert.True(RuntimeProcessState.AnyRunning([unknown]));
    }
}
