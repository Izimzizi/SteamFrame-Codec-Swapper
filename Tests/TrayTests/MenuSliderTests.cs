using SteamFrameTray;

namespace TrayTests;

public sealed class MenuSliderTests
{
    [Theory]
    [InlineData(351, 350)]
    [InlineData(343, 350)]
    [InlineData(340, 340)]
    [InlineData(376, 375)]
    [InlineData(498, 500)]
    [InlineData(100, 200)]
    [InlineData(900, 500)]
    [InlineData(205, 200)]
    [InlineData(260, 260)]
    public void Values_snap_to_every_50_when_close_and_otherwise_move_in_steps_of_5(int input, int expected) =>
        Assert.Equal(expected, MenuBitrateSlider.Snapped(input));
}
