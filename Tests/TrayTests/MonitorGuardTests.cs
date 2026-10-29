using SteamFrameTray;
using SteamFrame.Core;

namespace TrayTests;

public sealed class MonitorGuardTests
{
    [Fact]
    public async Task The_HEVC_monitor_is_not_injected_while_the_tray_is_armed()
    {
        var starts = 0;
        var armed = true;
        var snapshot = new EncoderSnapshot { ProcessId = 100, Supported = true, Message = "SteamVR is running." };
        var sampler = new StatsSampler(() => Task.FromResult(snapshot), () => { starts++; return Task.FromResult(snapshot); }, monitorAllowed: () => !armed);
        await sampler.SampleAsync();
        Assert.Equal(0, starts);
        armed = false;
        await sampler.SampleAsync();
        Assert.Equal(1, starts);
    }
}
