using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Tests;

public class StreamingExperienceTests
{
    private static LinkCapacityHistogram Capacity(long frames = 2500)
    {
        var counts = new long[71];
        counts[^1] = frames;
        return new(5, counts);
    }

    private static WirelessAdvisorInput Active => new()
    {
        StreamActive = true, DriverPatched = true, CurrentTargetMbps = 300,
        Histogram = Capacity(), HistogramAgeSeconds = 5, MeasuredVideoMbps = 290
    };

    [Theory]
    [InlineData(null)]
    [InlineData(-1.0)]
    [InlineData(96.0)]
    public void An_active_encoder_without_fresh_capacity_does_not_prove_headroom(double? age)
    {
        var advice = WirelessAdvisor.Advise(Active with { HistogramAgeSeconds = age });
        Assert.Null(advice.RecommendedMbps);
        Assert.False(advice.Headroom);
        Assert.Equal(AdviceConfidence.None, advice.Confidence);
    }

    [Fact]
    public void Empty_and_tiny_histograms_do_not_receive_high_confidence()
    {
        foreach (var frames in new long[] { 0, 99 })
        {
            var advice = WirelessAdvisor.Advise(Active with { Histogram = Capacity(frames) });
            Assert.Null(advice.RecommendedMbps);
            Assert.False(advice.Headroom);
        }
    }

    [Fact]
    public void A_stopped_stream_does_not_reuse_its_recent_histogram_or_faults()
    {
        var advice = WirelessAdvisor.Advise(Active with { StreamActive = false, ThrottleEventsLastMinute = 200 });
        Assert.Null(advice.RecommendedMbps);
        Assert.Equal(AdviceConfidence.None, advice.Confidence);
    }

    [Fact]
    public void A_light_scene_does_not_trigger_a_higher_target_but_link_trouble_still_reduces_it()
    {
        var light = Active with { MeasuredVideoMbps = 100 };
        var advice = WirelessAdvisor.Advise(light);
        Assert.Equal(300, advice.RecommendedMbps);
        Assert.False(advice.Headroom);
        Assert.Contains("not tested", advice.Headline);
        var troubled = WirelessAdvisor.Advise(light with { HeadsetLossRatio = 0.03 });
        Assert.True(troubled.LinkLimited);
        Assert.Equal(240, troubled.RecommendedMbps);
        Assert.True(WirelessAdvisor.Advise(Active).Headroom);
    }

    [Fact]
    public void Link_trouble_can_reduce_the_target_before_a_histogram_arrives()
    {
        var advice = WirelessAdvisor.Advise(Active with { Histogram = null, HeadsetLossRatio = 0.03 });
        Assert.True(advice.LinkLimited);
        Assert.Equal(240, advice.RecommendedMbps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(200.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_confirmed_but_undermeasured_sweep_is_never_an_applicable_ceiling(double? video)
    {
        var sweep = new SweepConclusion(400, null, "", video, Confirmed: true);
        Assert.True(sweep.LightlyTested);
        Assert.Null(sweep.ApplicableCeilingMbps);
        Assert.Null(WirelessAdvisor.PreferredSweepCeiling(WirelessAdvisor.Advise(Active), sweep, settling: true));
    }

    [Fact]
    public void A_clean_first_pass_or_a_quick_check_cannot_be_auto_applied()
    {
        var measured = new SweepConclusion(400, null, "", 380);
        Assert.Null(measured.ApplicableCeilingMbps);
        Assert.Equal(400, (measured with { Confirmed = true }).ApplicableCeilingMbps);
        Assert.Null((measured with { Confirmed = true, Check = true }).ApplicableCeilingMbps);
    }

    [Fact]
    public void New_link_trouble_and_missing_evidence_override_an_older_sweep_after_settling()
    {
        var sweep = new SweepConclusion(400, null, "", 380, Confirmed: true);
        var troubled = WirelessAdvisor.Advise(Active with { CurrentTargetMbps = 400, HeadsetLossRatio = 0.03 });
        Assert.Equal(400, WirelessAdvisor.PreferredSweepCeiling(troubled, sweep, settling: true));
        Assert.Null(WirelessAdvisor.PreferredSweepCeiling(troubled, sweep, settling: false));
        Assert.Null(WirelessAdvisor.PreferredSweepCeiling(WirelessAdvice.None, sweep, settling: false));
        var clean = WirelessAdvisor.Advise(Active with { CurrentTargetMbps = 400, MeasuredVideoMbps = 380 });
        Assert.Equal(400, WirelessAdvisor.PreferredSweepCeiling(clean, sweep, settling: false));
    }

    [Fact]
    public void Interrupted_confirmation_is_excluded_from_future_automatic_start_history()
    {
        var now = DateTimeOffset.Now;
        SweepRecord[] history = [new(now, 200, 90, ""), new(now, 400, 90, "", Confirmed: false)];
        Assert.False(history[1].IsFullyTested);
        Assert.Null(LinkCapacitySweep.AutomaticStart(history, now));
    }
}
