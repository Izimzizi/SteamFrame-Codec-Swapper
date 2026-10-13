using SteamLinkVRSettings.Frame;

public sealed class EncoderControlsTests
{
    [Fact]
    public void Policies_reject_unknown_fields_and_round_trip()
    {
        foreach (var rate in Enum.GetValues<EncoderRateControl>())
        foreach (var pass in Enum.GetValues<EncoderMultiPass>())
        {
            var policy = new EncodingPolicy(rate, pass);
            Assert.Equal(policy, EncodingPolicy.FromPacked(policy.Packed));
        }
        Assert.Equal(14u, new EncodingPolicy(EncoderRateControl.Cbr, EncoderMultiPass.FullResolution).Packed);
        foreach (uint invalid in new uint[] { 3, 7, 11, 15, 16, uint.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => EncodingPolicy.FromPacked(invalid));
    }
    [Fact]
    public void Refresh_restores_latest_target_and_preserves_external_changes()
    {
        double target = 180;
        OpenVrSettings.RefreshTarget(() => target, value => target = value, () => Assert.Equal(181, target));
        Assert.Equal(180, target);
        OpenVrSettings.RefreshTarget(() => target, value => target = value, () => target = 200);
        Assert.Equal(200, target); // An external, newer target is never overwritten.
        Assert.Throws<IOException>(() => OpenVrSettings.RefreshTarget(() => target, value => target = value, () => throw new IOException("test")));
        Assert.Equal(200, target); // A failed wait still restores our temporary value.
        target = 500;
        OpenVrSettings.RefreshTarget(() => target, value => target = value, () => Assert.Equal(499, target));
        Assert.Equal(500, target);
        target = double.NaN;
        Assert.Throws<InvalidOperationException>(() => OpenVrSettings.RefreshTarget(() => target, _ => throw new Exception("must not write"), () => { }));
    }
    [Fact]
    public void Whole_frame_quantizer_uses_interval_and_handles_generation_reset()
    {
        var tracker = new QuantizerTracker();
        Assert.Null(tracker.Sample("a", 100, 3600));
        Assert.Equal(34.0, tracker.Sample("a", 110, 3940));
        Assert.Null(tracker.Sample("a", 110, 3940));
        Assert.Null(tracker.Sample("b", 2, 70));
        Assert.Equal(35.0, tracker.Sample("b", 4, 140));
        Assert.Null(tracker.Sample("b", 1, 30));
        tracker.Reset(); Assert.Null(tracker.Sample("b", 2, 60));
    }
    [Fact]
    public async Task Power_lease_restores_after_crash_and_retains_user_changes()
    {
        var path = Path.Combine(Path.GetTempPath(), "frame-power-test-" + Guid.NewGuid().ToString("N"), "lease.json");
        var settings = new FakePower();
        try
        {
            var first = new TemporaryPowerLease(settings, path);
            await first.EnableAsync();
            Assert.True(first.Enabled); Assert.True(File.Exists(path)); Assert.False(settings.Pause); Assert.Equal(3600, settings.Timeout);
            settings.Timeout = 27; // User's newer value survives restoration.
            var afterCrash = new TemporaryPowerLease(settings, path);
            await afterCrash.RestoreAsync();
            Assert.True(settings.Pause); Assert.Equal(27, settings.Timeout); Assert.False(File.Exists(path));
            await afterCrash.EnableAsync(); settings.FailRestore = true;
            await Assert.ThrowsAsync<IOException>(() => afterCrash.RestoreAsync());
            Assert.True(File.Exists(path)); Assert.True(afterCrash.Enabled);
            settings.FailRestore = false; await afterCrash.RestoreAsync();
            Assert.True(settings.Pause); Assert.Equal(27, settings.Timeout); Assert.False(File.Exists(path));
            await afterCrash.RestoreAsync(); // No lease performs no power writes.
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, false); }
    }
    private sealed class FakePower : IFramePowerSettings
    {
        public string Identity => Path.Combine(Path.GetTempPath(), "frame-test-steam");
        public bool Pause = true, FailRestore;
        public int Timeout = 5;
        public (bool Pause, int Timeout) Read() => (Pause, Timeout);
        public void KeepAwake() { Pause = false; Timeout = 3600; }
        public void Restore(bool originalPause, int originalTimeout)
        {
            if (FailRestore) throw new IOException("retry");
            if (!Pause) Pause = originalPause;
            if (Timeout == 3600) Timeout = originalTimeout;
        }
    }
}
