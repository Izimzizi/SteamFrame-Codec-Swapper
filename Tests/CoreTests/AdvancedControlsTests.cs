using System.Buffers.Binary;
using SteamLinkVRSettings.Frame;

public sealed class AdvancedControlsTests
{
    [Fact]
    public void Native_request_preserves_missing_and_explicit_zero_fields()
    {
        var defaults = new AdvancedEncoding().ToNativeBytes();
        Assert.Equal(64, defaults.Length);
        Assert.Equal(64u, Read(defaults, 0)); Assert.Equal(1u, Read(defaults, 1)); Assert.Equal(0u, Read(defaults, 2));
        Assert.Equal(0u, Read(defaults, 14)); Assert.Equal(0u, Read(defaults, 15));
        var request = new AdvancedEncoding { QualityTarget = 0, MaximumQp = 0, SpatialAq = false, TemporalAq = false,
            CbQpOffset = -7, CrQpOffset = 3, PeakMbps = 250, VbvMbit = .001, VbvDelayMbit = 0, Preset = 2 };
        var bytes = request.ToNativeBytes();
        Assert.Equal((uint)request.Flags, Read(bytes, 2));
        Assert.Equal(0u, Read(bytes, 3)); Assert.Equal(0u, Read(bytes, 4)); Assert.Equal(250000000u, Read(bytes, 5));
        Assert.Equal(1000u, Read(bytes, 6)); Assert.Equal(0u, Read(bytes, 7));
        Assert.Equal(-7, unchecked((int)Read(bytes, 8))); Assert.Equal(3u, Read(bytes, 9));
        Assert.Equal(0u, Read(bytes, 10)); Assert.Equal(0u, Read(bytes, 12)); Assert.Equal(2u, Read(bytes, 13));
        Assert.True(request.Flags.HasFlag(AdvancedEncodingFlags.SpatialAq));
    }
    [Fact]
    public void Fractional_quality_request_is_canonicalized_before_readback_comparison()
    {
        var request = new AdvancedEncoding { QualityTarget = 1.3, PeakMbps = 200.0000006, VbvMbit = 1.2345678 };
        var canonical = request.Canonicalize(); var bytes = canonical.ToNativeBytes();
        Assert.Equal(Read(bytes, 3) / 256.0, canonical.QualityTarget);
        Assert.Equal(Read(bytes, 5) / 1_000_000.0, canonical.PeakMbps);
        Assert.Equal(Read(bytes, 6) / 1_000_000.0, canonical.VbvMbit);
    }
    [Fact]
    public void Invalid_budget_and_quantizer_requests_never_reach_native()
    {
        foreach (var request in new[]
        {
            new AdvancedEncoding { QualityTarget = double.NaN }, new AdvancedEncoding { QualityTarget = 51.01 },
            new AdvancedEncoding { MaximumQp = -1 }, new AdvancedEncoding { PeakMbps = 501 },
            new AdvancedEncoding { VbvMbit = 0 }, new AdvancedEncoding { VbvMbit = 1, VbvDelayMbit = 2 },
            new AdvancedEncoding { CbQpOffset = 0 }, new AdvancedEncoding { CbQpOffset = 13, CrQpOffset = 0 },
            new AdvancedEncoding { AqStrength = 16 }, new AdvancedEncoding { Preset = 8 }
        }) Assert.ThrowsAny<ArgumentException>(() => request.ToNativeBytes());
    }
    [Fact]
    public void Width_alignment_and_integer_types_are_validated_before_conversion()
    {
        foreach (var definition in AdvancedDriverSettings.Definitions.Where(d => d.Key.Contains("StreamFormatWidth", StringComparison.Ordinal)))
        {
            definition.Validate(1536d);
            Assert.Throws<ArgumentException>(() => definition.Validate(1536.5));
            Assert.Throws<ArgumentException>(() => definition.Validate(1537));
        }
        var retry = AdvancedDriverSettings.Definitions.Single(d => d.Key == "enableTimedRetry");
        Assert.Throws<ArgumentException>(() => retry.Validate(.5)); retry.Validate(0); retry.Validate(1);
        Assert.Equal(16, AdvancedDriverSettings.Definitions.Length);
    }
    private static uint Read(byte[] bytes, int index) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4, 4));
    [Fact]
    public void Unset_width_fallbacks_and_live_bound_values_are_enforced()
    {
        var empty = new Dictionary<string, object?>();
        Assert.Throws<ArgumentException>(() => AdvancedDriverSettings.ValidateWidthBounds(empty, empty,
            new Dictionary<string, object?> { ["minStreamFormatWidth"] = 1536 }, []));
        AdvancedDriverSettings.ValidateWidthBounds(empty, empty,
            new Dictionary<string, object?> { ["minStreamFormatWidth"] = 1536, ["maxStreamFormatWidth"] = 1792 }, []);
        Assert.Throws<ArgumentException>(() => AdvancedDriverSettings.ValidateWidthBounds(empty, empty,
            new Dictionary<string, object?> { ["minNonFoveatedStreamFormatWidth"] = 2048 }, []));
        AdvancedDriverSettings.ValidateWidthBounds(empty, empty,
            new Dictionary<string, object?> { ["minNonFoveatedStreamFormatWidth"] = 2048, ["maxNonFoveatedStreamFormatWidth"] = 2240 }, []);
        var live = new Dictionary<string, object?> { ["maxStreamFormatWidth"] = 1664 };
        var diskDefaults = new Dictionary<string, object?> { ["maxStreamFormatWidth"] = 4096 };
        Assert.Throws<ArgumentException>(() => AdvancedDriverSettings.ValidateWidthBounds(live, diskDefaults,
            new Dictionary<string, object?> { ["minStreamFormatWidth"] = 1792 }, []));
        AdvancedDriverSettings.ValidateWidthBounds(live, diskDefaults,
            new Dictionary<string, object?> { ["minStreamFormatWidth"] = 1792 }, ["maxStreamFormatWidth"]);
    }
}
