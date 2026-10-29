namespace SteamFrame.Core;

/// <summary>Interval mean of NVENC whole-frame QP metadata. This is never a gaze/center QP estimate.</summary>
public sealed class QuantizerTracker
{
    private string? _generation;
    private ulong _samples, _sum;
    public double? Sample(string generation, ulong samples, ulong sum)
    {
        double? mean = null;
        if (_generation == generation && samples > _samples && sum >= _sum)
        {
            var value = (sum - _sum) / (double)(samples - _samples);
            if (value is >= 0 and <= 63) mean = value;
        }
        _generation = generation; _samples = samples; _sum = sum;
        return mean;
    }
    public void Reset() => _generation = null;
}
