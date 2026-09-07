using UseNotch.Platform.Windows.Overlay;

namespace UseNotch.Windows.Tests;

public class OverlayPlacementCalculatorTests
{
    private static readonly DisplayMonitor Primary = new(
        "PRIMARY",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1040),
        1,
        true);

    [Theory]
    [InlineData(OverlayEdge.Right, 1844, 430)]
    [InlineData(OverlayEdge.Left, 0, 430)]
    [InlineData(OverlayEdge.Top, 922, 0)]
    [InlineData(OverlayEdge.Bottom, 922, 820)]
    public void Places_each_edge_against_the_correct_work_area_boundary(OverlayEdge edge, int expectedX, int expectedY)
    {
        var placement = OverlayPlacementCalculator.Calculate(Primary, edge, new DipSize(76, 220));

        Assert.Equal(new PixelPoint(expectedX, expectedY), placement.Position);
        Assert.Equal(new PixelSize(76, 220), placement.Size);
    }

    [Fact]
    public void Retains_negative_secondary_coordinates_and_converts_dips_to_pixels()
    {
        var secondary = new DisplayMonitor(
            "SECONDARY",
            new PixelRect(-2560, 120, 2560, 1440),
            new PixelRect(-2560, 120, 2560, 1380),
            1.5,
            false);

        var placement = OverlayPlacementCalculator.Calculate(secondary, OverlayEdge.Right, new DipSize(76, 220));

        Assert.Equal(new PixelSize(114, 330), placement.Size);
        Assert.Equal(new PixelPoint(-114, 675), placement.Position);
    }

    [Fact]
    public void Selects_the_primary_monitor_when_a_preferred_monitor_is_removed()
    {
        var remaining = new[] { Primary };

        var selected = OverlayPlacementCalculator.SelectMonitor(remaining, "REMOVED");

        Assert.Equal(Primary, selected);
    }

    [Fact]
    public void Pixel_rect_has_half_open_hit_boundaries()
    {
        var rect = new PixelRect(-20, 10, 40, 30);

        Assert.True(rect.Contains(new PixelPoint(-20, 10)));
        Assert.True(rect.Contains(new PixelPoint(19, 39)));
        Assert.False(rect.Contains(new PixelPoint(20, 39)));
        Assert.False(rect.Contains(new PixelPoint(19, 40)));
    }
}
