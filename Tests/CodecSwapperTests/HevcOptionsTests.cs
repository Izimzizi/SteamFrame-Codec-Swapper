using SteamFrame.Core;
using CodecSwapper;

namespace CodecSwapperTests;

public sealed class HevcOptionsTests
{
    [Fact]
    public void The_defaults_reach_the_stock_stream_too()
    {
        Assert.Equal("enabled=1\nrate_control=vbr\npreset=2\nvbv=2\nmultipass=0\n", HevcOptions.Text(new FramePreferences()));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 2)]
    [InlineData(-3, 0)]
    public void Multipass_is_written_and_kept_between_off_and_full_resolution(int chosen, int written) =>
        Assert.Contains($"multipass={written}\n", HevcOptions.Text(new FramePreferences { HookMultiPass = chosen }));

    [Fact]
    public void The_file_carries_the_chosen_options_and_keeps_them_in_range()
    {
        var text = HevcOptions.Text(new FramePreferences { HookRateControl = "cbr", HookPreset = 9, HookVbvFrames = 0 });
        Assert.Equal("enabled=1\nrate_control=cbr\npreset=7\nvbv=1\nmultipass=0\n", text);
    }

    [Fact]
    public void Writing_replaces_the_file_without_leaving_a_temporary_one()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stock-options-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "hevc-options.txt");
            HevcOptions.Write(path, new FramePreferences());
            HevcOptions.Write(path, new FramePreferences { HookPreset = 5 });
            Assert.Contains("preset=5", File.ReadAllText(path));
            Assert.Equal([path], Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}
