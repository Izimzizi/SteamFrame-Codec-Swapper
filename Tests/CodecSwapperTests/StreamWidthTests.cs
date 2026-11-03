using CodecSwapper;

namespace CodecSwapperTests;

public sealed class StreamWidthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "streamwidth-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "steamvr.vrsettings");
    public StreamWidthTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData(960, 960)]
    [InlineData(500, 960)]
    [InlineData(1000, 992)]
    [InlineData(1344, 1344)]
    [InlineData(1360, 1344)]
    [InlineData(1361, 1376)]
    [InlineData(1920, 1920)]
    [InlineData(2040, 2048)]
    [InlineData(5000, 2048)]
    public void Widths_are_multiples_of_32_inside_the_slider_range(int input, int expected) =>
        Assert.Equal(expected, StreamWidth.Snapped(input));

    [Fact]
    public void The_range_is_SteamLinks_own_start_up_to_what_HEVC_takes_and_H264_stays_at_1024()
    {
        Assert.Equal(960, StreamWidth.Minimum);
        Assert.Equal(2048, StreamWidth.Maximum);            // 4 x 2048 = 8192 rows, the most NVENC HEVC takes
        Assert.Equal(1024, StreamWidth.H264Limit);
        Assert.Equal(0, StreamWidth.Minimum % 32); Assert.Equal(0, StreamWidth.Maximum % 32);
    }

    [Fact]
    public void Write_sets_the_width_turns_automatic_off_and_keeps_every_other_key()
    {
        File.WriteAllText(File_, "{\n  \"steamvr\": {\"x\": 1},\n  \"driver_vrlink\": {\"automaticStreamFormatWidth\": true, \"streamFormatWidth\": 1344, \"targetBandwidth\": 350}\n}");
        StreamWidth.WriteFile(File_, 1920);
        Assert.Equal(1920, StreamWidth.ReadFile(File_));
        var text = File.ReadAllText(File_);
        Assert.Contains("\"automaticStreamFormatWidth\": false", text); Assert.Contains("\"targetBandwidth\": 350", text); Assert.Contains("\"x\": 1", text);
        Assert.Empty(Directory.GetFiles(_dir, "*.tray-*"));
    }

    [Fact]
    public void Write_creates_the_file_or_section_when_missing() { StreamWidth.WriteFile(File_, 2048); Assert.Equal(2048, StreamWidth.ReadFile(File_)); }

    [Fact]
    public void An_unreadable_file_is_reported_and_left_unchanged()
    {
        File.WriteAllText(File_, "{ not json");
        Assert.Throws<IOException>(() => StreamWidth.WriteFile(File_, 1920));
        Assert.Equal("{ not json", File.ReadAllText(File_));
        Assert.Null(StreamWidth.ReadFile(File_));
    }

    [Fact]
    public void The_restore_file_the_hook_writes_is_read_with_its_width_and_automatic_flag()
    {
        var restore = Path.Combine(_dir, "width-restore.txt");
        Assert.Null(StreamWidth.ReadRestore(restore));
        File.WriteAllText(restore, "1344 0" + Environment.NewLine);
        Assert.Equal((1344, 0), StreamWidth.ReadRestore(restore));
        File.WriteAllText(restore, "garbage");
        Assert.Null(StreamWidth.ReadRestore(restore));
    }

    [Fact]
    public async Task While_H264_holds_a_lowered_width_the_display_shows_the_users_own()
    {
        var restore = Path.Combine(_dir, "width-restore.txt");
        File.WriteAllText(restore, "1600 1" + Environment.NewLine);
        Assert.Equal(1600, await StreamWidth.ReadAsync(new SteamFrame.Core.SteamPaths(_dir, _dir), restore));
    }

    [Fact]
    public void A_missing_value_reads_as_null() { File.WriteAllText(File_, "{\"driver_vrlink\": {}}"); Assert.Null(StreamWidth.ReadFile(File_)); }
}
