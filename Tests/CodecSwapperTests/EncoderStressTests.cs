using CodecSwapper;

namespace CodecSwapperTests;

public sealed class EncoderStressTests
{
    private static readonly EncoderChoice Start = new("vbr", 2, 1, 0);

    private static List<EncoderChoice> Run(int seed, int every, int count)
    {
        var plan = new EncoderStressPlan(seed, every);
        var list = new List<EncoderChoice>();
        var current = Start;
        for (var i = 0; i < count; i++) { current = plan.Next(current); list.Add(current); }
        return list;
    }

    [Fact]
    public void The_same_seed_gives_the_same_changes()
    {
        Assert.Equal(Run(7, 3, 40), Run(7, 3, 40));
        Assert.NotEqual(Run(7, 3, 40), Run(8, 3, 40));
    }

    [Fact]
    public void Every_nth_change_moves_multipass_through_0_1_2_and_nothing_else_does()
    {
        var plan = new EncoderStressPlan(3, 3);
        var current = Start;
        for (var change = 1; change <= 60; change++)
        {
            var next = plan.Next(current);
            if (change % 3 == 0)
            {
                Assert.NotEqual(current.MultiPass, next.MultiPass);
                Assert.InRange(next.MultiPass, 0, 2);
                Assert.Equal(current with { MultiPass = next.MultiPass }, next);
            }
            else
            {
                Assert.Equal(current.MultiPass, next.MultiPass);
                Assert.NotEqual(current, next);
            }
            Assert.InRange(next.Preset, 1, 7); Assert.InRange(next.VbvFrames, 1, 8); Assert.Contains(next.RateControl, new[] { "vbr", "cbr" });
            current = next;
        }
    }

    [Fact]
    public void Multipass_walks_0_1_2_in_order_and_also_jumps_back()
    {
        var plan = new EncoderStressPlan(5, 1);
        var current = Start with { MultiPass = 0 };
        var steps = new HashSet<(int, int)>();
        for (var i = 0; i < 200; i++) { var next = plan.Next(current); steps.Add((current.MultiPass, next.MultiPass)); current = next; }
        Assert.Contains((0, 1), steps); Assert.Contains((1, 2), steps); Assert.Contains((2, 0), steps);
        Assert.Contains((2, 1), steps);
    }

    [Fact]
    public void A_fatal_line_after_the_start_is_found_and_an_older_one_is_not()
    {
        var path = Path.Combine(Path.GetTempPath(), "driver-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "Wed Nov 04 2026 15:25:03.787 [Fatal] - Error: Perf: Frame 5 not in expected state.\nWed Nov 04 2026 15:25:04.000 [Info] - fine\n");
            Assert.Equal("Error: Perf: Frame 5 not in expected state.", EncoderStressSigns.DriverFatalSince(path, new DateTimeOffset(2026, 11, 4, 15, 25, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 4)))));
            Assert.Null(EncoderStressSigns.DriverFatalSince(path, new DateTimeOffset(2026, 11, 4, 15, 26, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 4)))));
            Assert.Null(EncoderStressSigns.DriverFatalSince(path + ".missing", DateTimeOffset.MinValue));
        }
        finally { File.Delete(path); }
    }
}
