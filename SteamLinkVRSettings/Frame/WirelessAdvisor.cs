namespace SteamLinkVRSettings.Frame;

public enum AdviceConfidence { None, Low, Medium, High }

/// <summary>Everything the advisor looks at. Null means the measurement is unavailable, never zero.</summary>
public sealed record WirelessAdvisorInput
{
    public double? CurrentTargetMbps { get; init; }
    public bool AutomaticBitrate { get; init; }
    public bool PairedCodec { get; init; }
    public bool DriverPatched { get; init; }
    public bool DriverVerified { get; init; } = true;
    public bool UsbSlow { get; init; }
    public WirelessLinkSnapshot? Radio { get; init; }
    public LinkCapacityHistogram? Histogram { get; init; }
    public double? HistogramAgeSeconds { get; init; }
    public long ThrottleEventsLastMinute { get; init; }
    public double ThrottleMsLastMinute { get; init; }
    /// <summary>Display refresh used to scale held-frame counts into a share of frames.</summary>
    public int RefreshHz { get; init; } = 90;
    public StreamingHealthEvent? LatestBitrateChange { get; init; }
    public double? BitrateChangeAgeSeconds { get; init; }
    public DriverBitrateReading? DriverBitrate { get; init; }
    public double? DriverBitrateAgeSeconds { get; init; }
    public double? HeadsetLossRatio { get; init; }
    public double? HeadsetLatencyMs { get; init; }
    public double? MeasuredVideoMbps { get; init; }
    public bool StreamActive { get; init; }
}

public sealed record WirelessAdvice(int? RecommendedMbps, string Headline, IReadOnlyList<string> Reasons, AdviceConfidence Confidence, bool LinkLimited, bool Headroom)
{
    public static readonly WirelessAdvice None = new(null, "No recommendation yet.", [], AdviceConfidence.None, false, false);
}

/// <summary>
/// Turns the measurements the app already collects into one suggested manual target with reasons.
/// Every rule is a heuristic over observed data; nothing here is a guarantee of picture quality.
/// </summary>
public static class WirelessAdvisor
{
    public const int StockCeilingMbps = 232, PatchedCeilingMbps = 500, Usb2CeilingMbps = 250, ValveDefaultStartMbps = 200;
    public const double FreshSeconds = 95;
    /// <summary>
    /// Held frames as a share of frames per minute. Auto tune treats about 5% affected frames as serious;
    /// here 2% marks a link that is limiting the stream and 0.5% marks occasional held frames. A measured
    /// 6 GHz link held 0.2% of frames at 250 Mbps while its capacity histogram sat above 350 Mbps, so
    /// anything stricter blames the bitrate for ordinary radio jitter.
    /// </summary>
    public const double StrongHeldShare = 0.02, MildHeldShare = 0.005;
    /// <summary>Held time per minute (ms): 2% of the minute is limiting, 0.5% is occasional.</summary>
    public const double StrongHeldMsPerMinute = 1200, MildHeldMsPerMinute = 300;
    /// <summary>Worst-case headset link latency that marks queueing on the link.</summary>
    public const double LatencyNoticeMs = 50;

    public static (double Strong, double Mild) HeldFrameLimits(int refreshHz)
    {
        var frames = Math.Clamp(refreshHz, 60, 144) * 60.0;
        return (frames * StrongHeldShare, frames * MildHeldShare);
    }
    public static bool HeldFramesLimiting(long perMinute, double msPerMinute, int refreshHz) => perMinute >= HeldFrameLimits(refreshHz).Strong || msPerMinute >= StrongHeldMsPerMinute;
    public static bool HeldFramesNoticeable(long perMinute, double msPerMinute, int refreshHz) => perMinute >= HeldFrameLimits(refreshHz).Mild || msPerMinute >= MildHeldMsPerMinute;

    /// <summary>Rounds to 5 Mbps, keeping the driver ceilings themselves reachable.</summary>
    public static int Round5(double mbps)
    {
        if (Math.Abs(mbps - StockCeilingMbps) < 0.5) return StockCeilingMbps;
        return (int)Math.Clamp(Math.Round(mbps / 5) * 5, 25, PatchedCeilingMbps);
    }

    public static WirelessAdvice Advise(WirelessAdvisorInput input)
    {
        var reasons = new List<string>();
        if (input.PairedCodec)
            return new(null, "The advisor covers HEVC streams. AV1 and H.264 sessions keep their own limits.", reasons, AdviceConfidence.None, false, false);

        var ceiling = input.DriverPatched ? PatchedCeilingMbps : StockCeilingMbps;
        if (!input.DriverPatched)
            reasons.Add(input.DriverVerified ? "The stock driver caps wireless streams at 232 Mbps; install the bitrate override under Patches & Recovery for more."
                : "The driver could not be verified, so a 232 Mbps ceiling is assumed.");
        if (input.UsbSlow)
        {
            ceiling = Math.Min(ceiling, Usb2CeilingMbps);
            reasons.Add("The dongle is on a USB 2 connection; move it to a USB 3 port before expecting more than about 250 Mbps.");
        }

        var radio = input.Radio;
        var radioCap = int.MaxValue;
        if (radio is { Connected: true })
        {
            if (radio.LegacyBand) { radioCap = 100; reasons.Add("The dongle is associated on 2.4 GHz; the Frame's dedicated link should be on 6 GHz. Check the dongle placement and that nothing is forcing 2.4 GHz."); }
            if (radio.Weak) reasons.Add($"Radio signal is weak ({(radio.Rssi is int rssi ? $"{rssi} dBm" : $"{radio.SignalQuality}%")}). Move the dongle into line of sight of the headset, away from the PC case.");
            if (radio.Narrow) reasons.Add($"The radio link is using a {radio.BandwidthMhz} MHz channel; 160 MHz carries far more. Interference or a DFS/regulatory limit may be narrowing it.");
            if (radio.ReceiveRateMbps is double phy)
            {
                var usable = Round5(phy * 0.5);
                if (radio.SlowPhy) reasons.Add($"The PHY rate is only {phy:0} Mbps; real throughput is roughly half of that.");
                radioCap = Math.Min(radioCap, Math.Max(50, usable));
            }
            if (radio.RetryShare is double share && share > 0.15) reasons.Add($"{share:P0} of radio frames needed retries since the last poll, a sign of interference or a marginal signal.");
        }

        var histogram = input.Histogram;
        var histogramFresh = histogram is not null && input.HistogramAgeSeconds is <= FreshSeconds;
        double? capacityP10 = null; bool capacityOpenEnded = false;
        if (histogramFresh && histogram!.TotalSamples >= 100)
        {
            var p10 = histogram.Percentile(0.10);
            capacityP10 = p10.Mbps; capacityOpenEnded = p10.AtOrAbove;
            reasons.Add(capacityOpenEnded
                ? $"SteamVR measured {histogram.OverflowShare:P0} of recent frames at {histogram.OverflowEdgeMbps:0} Mbps or more of link capacity; the histogram cannot see above that."
                : $"SteamVR's link measurements put the 10th percentile of recent frames at {p10.Mbps:0} Mbps ({histogram.ShareBelow(100):P0} of frames below 100 Mbps).");
        }

        var target = input.CurrentTargetMbps;
        var throttles = input.ThrottleEventsLastMinute;
        var heldLimiting = HeldFramesLimiting(throttles, input.ThrottleMsLastMinute, input.RefreshHz);
        var heldNoticeable = HeldFramesNoticeable(throttles, input.ThrottleMsLastMinute, input.RefreshHz);
        var stepDown = input.LatestBitrateChange is { ChangedBitrate: true } change && change.NewBitrateMbps < change.PreviousBitrateMbps && input.BitrateChangeAgeSeconds is <= 120 ? change : null;
        var strong = heldLimiting || stepDown is not null || input.HeadsetLossRatio is > 0.01;
        var mild = !strong && (heldNoticeable || input.HeadsetLossRatio is > 0.002 || input.HeadsetLatencyMs is > LatencyNoticeMs);
        if (heldNoticeable)
        {
            var share = throttles / (Math.Clamp(input.RefreshHz, 60, 144) * 60.0);
            reasons.Add($"{throttles} frames were held back by the sender in the last minute ({share:P2} of frames, {input.ThrottleMsLastMinute:0} ms){(target is double t ? $" at a {t:0} Mbps target" : "")}; " +
                (heldLimiting ? "at this rate the link is holding the stream back." : "occasional held frames, not yet a limiting link."));
        }
        if (stepDown is not null) reasons.Add($"SteamVR lowered the stream from {stepDown.PreviousBitrateMbps:0} to {stepDown.NewBitrateMbps:0} Mbps after a {stepDown.DurationMs:0} ms link stall {Age(input.BitrateChangeAgeSeconds)}.");
        if (input.HeadsetLossRatio is > 0.002) reasons.Add($"The headset reports {input.HeadsetLossRatio:P2} packet loss on the active link.");
        if (input.HeadsetLatencyMs is > LatencyNoticeMs) reasons.Add($"The headset reports {input.HeadsetLatencyMs:0.#} ms worst-case link latency; queued packets usually mean the link is near its limit.");
        if (input.AutomaticBitrate && input.DriverBitrate is { } driver)
            reasons.Add($"Automatic bitrate is on; SteamVR's own estimate is {driver.Mbps:0} Mbps. The recommendation pins a manual target instead of letting it drift.");

        if (!input.StreamActive && !histogramFresh && throttles == 0)
        {
            reasons.Insert(0, "Start a wireless stream; SteamVR reports link capacity every 30 seconds while it runs.");
            return new(null, "Waiting for stream measurements.", reasons, AdviceConfidence.None, false, false);
        }

        // Capacity-based base: resolved percentile with headroom, or the open-ended edge.
        double? baseline = capacityP10 is double p
            ? capacityOpenEnded ? p : Math.Max(50, p * 0.85)
            : null;
        int recommended;
        bool linkLimited = false, headroom = false;
        if (strong)
        {
            linkLimited = true;
            var reduced = target is double t ? t * 0.8 : baseline ?? ValveDefaultStartMbps;
            if (stepDown?.NewBitrateMbps is double after) reduced = Math.Min(reduced, after);
            if (baseline is double b) reduced = Math.Min(reduced, b);
            recommended = Round5(reduced);
        }
        else if (mild)
        {
            var hold = target ?? baseline ?? ValveDefaultStartMbps;
            if (baseline is double b && b < hold) hold = b;
            recommended = Round5(hold);
        }
        else if (target is double t)
        {
            var limit = Math.Min(ceiling, radioCap);
            var upper = baseline is double b && !capacityOpenEnded ? Math.Min(limit, b) : limit;
            if (t + 5 <= upper)
            {
                headroom = true;
                recommended = Round5(Math.Min(upper, t + (upper - t > 100 ? 50 : 25)));
                reasons.Add(capacityOpenEnded || baseline is null
                    ? "No link stalls, held frames or loss in the last minute: there is headroom to step the target up and re-check."
                    : $"The link stayed clean at {t:0} Mbps and its measured 10th percentile allows about {upper:0} Mbps.");
            }
            else if (t > upper + 2.5 && t <= limit)
            {
                // Clean right now, but the measured capacity floor sits below the target.
                linkLimited = true;
                recommended = Round5(upper);
                reasons.Add($"The link's measured 10th percentile supports about {upper:0} Mbps; the {t:0} Mbps target is above what it carried in the last 30 s.");
            }
            else
            {
                recommended = Round5(Math.Min(t, upper));
                reasons.Add(t >= limit ? $"The target is already at the {limit} Mbps limit for this setup." : "The link is clean at the current target; keep it.");
            }
        }
        else recommended = Round5(Math.Min(baseline ?? ValveDefaultStartMbps, Math.Min(ceiling, radioCap)));

        recommended = Math.Min(recommended, Math.Min(ceiling, radioCap));
        recommended = Math.Max(recommended, 25);
        var confidence = histogramFresh && (input.StreamActive || throttles > 0) ? AdviceConfidence.High
            : input.StreamActive || histogramFresh ? AdviceConfidence.Medium : AdviceConfidence.Low;
        var keeping = target is double current && Math.Abs(current - recommended) < 2.5;
        var headline = linkLimited ? $"The link is holding the stream back at the current rate; try {recommended} Mbps."
            : headroom ? $"Headroom available; try {recommended} Mbps and watch held frames."
            : keeping && mild ? $"Keep {recommended} Mbps for now; the link shows occasional held frames or latency at this rate."
            : keeping ? $"Keep {recommended} Mbps; the link is clean at this rate."
            : $"Suggested manual target: {recommended} Mbps.";
        if (recommended > ValveDefaultStartMbps) reasons.Add($"That is {(recommended - ValveDefaultStartMbps) * 100 / ValveDefaultStartMbps}% above Valve's default 200 Mbps starting rate, which the automatic mode then adjusts from.");
        return new(recommended, headline, reasons, confidence, linkLimited, headroom);
    }

    private static string Age(double? seconds) => seconds is double s ? s < 60 ? $"{s:0} s ago" : $"{s / 60:0} min ago" : "recently";
}
