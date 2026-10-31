using SteamFrame.Core;
using SteamFrameTray;

namespace TrayTests;

public sealed class StockOptionsTests
{
    [Fact]
    public void The_defaults_reach_the_stock_stream_too()
    {
        Assert.Equal("enabled=1\nrate_control=vbr\npreset=2\nvbv=2\n", StockOptions.Text(new FramePreferences()));
    }

    [Fact]
    public void The_file_carries_the_chosen_options_and_keeps_them_in_range()
    {
        var text = StockOptions.Text(new FramePreferences { HookRateControl = "cbr", HookPreset = 9, HookVbvFrames = 0 });
        Assert.Equal("enabled=1\nrate_control=cbr\npreset=7\nvbv=1\n", text);
    }

    [Fact]
    public void Writing_replaces_the_file_without_leaving_a_temporary_one()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stock-options-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "stock-options.txt");
            StockOptions.Write(path, new FramePreferences());
            StockOptions.Write(path, new FramePreferences { HookPreset = 5 });
            Assert.Contains("preset=5", File.ReadAllText(path));
            Assert.Equal([path], Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}
