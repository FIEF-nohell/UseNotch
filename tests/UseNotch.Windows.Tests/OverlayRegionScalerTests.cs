using UseNotch.Platform.Windows.Overlay;

namespace UseNotch.Windows.Tests;

public class OverlayRegionScalerTests
{
    private static OverlayRegionSnapshot Snapshot(params DipRect[] regions)
        => new(new DipSize(380, 260), regions);

    [Fact]
    public void At_one_hundred_percent_the_region_keeps_its_device_independent_position()
    {
        var regions = OverlayRegionScaler.ToClientPixels(Snapshot(new DipRect(316, 124, 64, 12)), new PixelSize(380, 260));

        var region = Assert.Single(regions);
        Assert.Equal(new PixelRect(316, 124, 64, 12), region);
    }

    [Fact]
    public void A_scaled_client_area_scales_the_region_by_the_real_ratio()
    {
        // 175 percent: the client area is 665x455 pixels for the same 380x260 device-independent surface.
        var regions = OverlayRegionScaler.ToClientPixels(Snapshot(new DipRect(316, 124, 64, 12)), new PixelSize(665, 455));

        var region = Assert.Single(regions);
        Assert.Equal(553, region.X);
        Assert.Equal(217, region.Y);
        Assert.Equal(112, region.Width);
        Assert.Equal(21, region.Height);
    }

    [Theory]
    [InlineData(570, 390)]
    [InlineData(760, 520)]
    public void A_scaled_region_stays_inside_the_client_area(int clientWidth, int clientHeight)
    {
        // The regression this guards: a conversion that used a mismatched scaling factor placed regions
        // outside the window, which silently disabled every interactive control.
        var snapshot = Snapshot(new DipRect(316, 124, 64, 12), new DipRect(0, 0, 380, 260));

        var regions = OverlayRegionScaler.ToClientPixels(snapshot, new PixelSize(clientWidth, clientHeight));

        Assert.All(regions, region =>
        {
            Assert.InRange(region.X, 0, clientWidth);
            Assert.InRange(region.Y, 0, clientHeight);
            Assert.InRange(region.X + region.Width, 0, clientWidth);
            Assert.InRange(region.Y + region.Height, 0, clientHeight);
        });
    }

    [Fact]
    public void A_full_surface_region_covers_the_whole_client_area_at_any_scale()
    {
        var regions = OverlayRegionScaler.ToClientPixels(Snapshot(new DipRect(0, 0, 380, 260)), new PixelSize(665, 455));

        var region = Assert.Single(regions);
        Assert.Equal(new PixelRect(0, 0, 665, 455), region);
    }

    [Theory]
    [InlineData(0, 260)]
    [InlineData(380, 0)]
    public void An_unusable_client_size_produces_no_regions_rather_than_a_wrong_one(int width, int height)
        => Assert.Empty(OverlayRegionScaler.ToClientPixels(Snapshot(new DipRect(1, 1, 2, 2)), new PixelSize(width, height)));

    [Fact]
    public void An_empty_snapshot_stays_empty()
        => Assert.Empty(OverlayRegionScaler.ToClientPixels(Snapshot(), new PixelSize(665, 455)));

    [Fact]
    public void A_snapshot_without_a_usable_surface_produces_no_regions()
        => Assert.Empty(OverlayRegionScaler.ToClientPixels(
            new OverlayRegionSnapshot(new DipSize(0, 0), [new DipRect(1, 1, 2, 2)]),
            new PixelSize(665, 455)));

    [Fact]
    public void Every_scaled_region_keeps_a_clickable_size()
    {
        var regions = OverlayRegionScaler.ToClientPixels(Snapshot(new DipRect(316.4, 124.6, 64, 12)), new PixelSize(665, 455));

        var region = Assert.Single(regions);
        Assert.True(region.Width > 0);
        Assert.True(region.Height > 0);
    }
}
