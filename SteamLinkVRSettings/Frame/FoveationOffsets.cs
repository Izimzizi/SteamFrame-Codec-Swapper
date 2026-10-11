namespace SteamLinkVRSettings.Frame;

public static class FoveationOffsets
{
    public const int Minimum = -12;
    public const int MinimumGaze = -20;
    public const int MaximumGaze = 12;
    public const int MaximumPeripheral = 31;

    public static bool IsValid(double value, int maximum, int minimum = Minimum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum && value == Math.Truncate(value);
}
