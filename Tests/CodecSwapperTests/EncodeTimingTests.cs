using CodecSwapper;
using Xunit;

namespace CodecSwapperTests;

public sealed class EncodeTimingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);
    private static string File(DateTimeOffset at, string codec = "H.264", int frames = 72, string average = "2.31", string max = "3.40") =>
        $"unix_ms={at.ToUnixTimeMilliseconds()}\ncodec={codec}\nframes={frames}\navg_ms={average}\nmax_ms={max}\n";

    [Fact]
    public void A_fresh_reading_is_parsed()
    {
        var timing = EncodeTiming.Parse(File(Now.AddSeconds(-1)), Now);
        Assert.Equal(new EncodeTiming("H.264", 72, 2.31, 3.40), timing);
        Assert.Equal("encode 2.3 ms avg / 3.4 ms max", timing!.Describe());
    }

    [Fact]
    public void A_stale_or_future_reading_is_ignored_because_the_stream_stopped()
    {
        Assert.Null(EncodeTiming.Parse(File(Now.AddSeconds(-10)), Now));
        Assert.Null(EncodeTiming.Parse(File(Now.AddSeconds(10)), Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("unix_ms=abc\ncodec=HEVC\nframes=1\navg_ms=1\nmax_ms=1\n")]
    public void Unreadable_text_gives_no_reading(string text) => Assert.Null(EncodeTiming.Parse(text, Now));

    [Fact]
    public void A_reading_without_frames_or_with_bad_numbers_is_ignored()
    {
        Assert.Null(EncodeTiming.Parse(File(Now, frames: 0), Now));
        Assert.Null(EncodeTiming.Parse(File(Now, average: "fast"), Now));
        Assert.Null(EncodeTiming.Parse(File(Now, codec: ""), Now));
    }

    [Fact]
    public void The_decimal_point_is_always_a_dot()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var timing = EncodeTiming.Parse(File(Now, average: "0.52", max: "0.61"), Now);
            Assert.Equal(0.52, timing!.AverageMs);
            Assert.Equal("encode 0.5 ms avg / 0.6 ms max", timing.Describe());
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    public void A_missing_file_gives_no_reading() => Assert.Null(EncodeTiming.Read(Path.Combine(Path.GetTempPath(), "no-such-encode-timing.txt"), Now));

    [Fact]
    public async Task The_sampler_attaches_the_reading_to_every_sample()
    {
        var timing = new EncodeTiming("HEVC", 90, 1.5, 2.0);
        var sampler = new StatsSampler(() => Task.FromResult(new SteamFrame.Core.EncoderSnapshot { Message = "SteamVR is not running." }), () => throw new InvalidOperationException(), now: () => Now, timing: at => at == Now ? timing : null);
        Assert.Equal(timing, (await sampler.SampleAsync()).Timing);
    }
}
