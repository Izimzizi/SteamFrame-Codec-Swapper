using System.Text.RegularExpressions;

namespace SteamLinkVRSettings.Frame;

/// <summary>The Frame's SSH address: its hostname (normally <c>frame</c>) or an IPv4 address, such as the dongle's 10.35.78.1.</summary>
public static partial class FrameAddress
{
    public const string DefaultHost = "frame";
    public const string DongleHost = "10.35.78.1";

    /// <summary>Same rule as the supervisor's <c>valid_host</c>: nothing ssh could read as an option, a user, a port or a path.</summary>
    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= 253 } && HostPattern().IsMatch(value) && !value.Contains("..", StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$")]
    private static partial Regex HostPattern();
}
