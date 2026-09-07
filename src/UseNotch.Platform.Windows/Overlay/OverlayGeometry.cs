namespace UseNotch.Platform.Windows.Overlay;

public enum OverlayEdge
{
    Top,
    Right,
    Bottom,
    Left,
}

public readonly record struct PixelPoint(int X, int Y);

public readonly record struct PixelSize(int Width, int Height)
{
    public static PixelSize FromDip(DipSize size, double scaling) =>
        new(
            Math.Max(1, (int)Math.Ceiling(size.Width * scaling)),
            Math.Max(1, (int)Math.Ceiling(size.Height * scaling)));
}

public readonly record struct DipSize(double Width, double Height);

public readonly record struct DipRect(double X, double Y, double Width, double Height);

/// <summary>
/// What the overlay window reports about its interactive surface, in its own device-independent
/// coordinates. The platform converts to pixels using the window's real client size, so a stale or
/// mismatched render scaling can never place a region outside the window.
/// </summary>
public readonly record struct OverlayRegionSnapshot(DipSize ClientSize, IReadOnlyList<DipRect> Regions);

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(PixelPoint point) =>
        point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;
}

public sealed record DisplayMonitor(
    string Id,
    PixelRect Bounds,
    PixelRect WorkingArea,
    double Scaling,
    bool IsPrimary);

public readonly record struct OverlayPlacement(PixelPoint Position, PixelSize Size);

public static class OverlayPlacementCalculator
{
    public static OverlayPlacement Calculate(DisplayMonitor monitor, OverlayEdge edge, DipSize desiredSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(desiredSize.Width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(desiredSize.Height, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(monitor.Scaling, 0);

        var size = PixelSize.FromDip(desiredSize, monitor.Scaling);
        var bounds = monitor.Bounds;
        var workArea = monitor.WorkingArea;
        var x = edge switch
        {
            OverlayEdge.Right => workArea.Right - size.Width,
            OverlayEdge.Left => workArea.X,
            _ => bounds.X + ((bounds.Width - size.Width) / 2),
        };
        var y = edge switch
        {
            OverlayEdge.Top => workArea.Y,
            OverlayEdge.Bottom => workArea.Bottom - size.Height,
            _ => bounds.Y + ((bounds.Height - size.Height) / 2),
        };

        return new OverlayPlacement(new PixelPoint(x, y), size);
    }

    public static DisplayMonitor SelectMonitor(
        IReadOnlyList<DisplayMonitor> monitors,
        string? preferredMonitorId)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("Windows did not report an available monitor.");
        }

        if (!string.IsNullOrWhiteSpace(preferredMonitorId))
        {
            var selected = monitors.FirstOrDefault(monitor =>
                string.Equals(monitor.Id, preferredMonitorId, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                return selected;
            }
        }

        return monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
    }
}

/// <summary>
/// Converts the overlay's device-independent regions into client pixels using the real client size of
/// the window. A reported render scaling is deliberately not used: Avalonia can report one that does not
/// match this window, which would place every region outside it and silently disable click-through.
/// </summary>
public static class OverlayRegionScaler
{
    public static IReadOnlyList<PixelRect> ToClientPixels(OverlayRegionSnapshot snapshot, PixelSize clientSize)
    {
        if (snapshot.Regions.Count == 0
            || snapshot.ClientSize.Width <= 0
            || snapshot.ClientSize.Height <= 0
            || clientSize.Width <= 0
            || clientSize.Height <= 0)
        {
            return [];
        }

        var scaleX = clientSize.Width / snapshot.ClientSize.Width;
        var scaleY = clientSize.Height / snapshot.ClientSize.Height;
        var regions = new List<PixelRect>(snapshot.Regions.Count);
        foreach (var region in snapshot.Regions)
        {
            var x = (int)Math.Floor(region.X * scaleX);
            var y = (int)Math.Floor(region.Y * scaleY);
            var right = (int)Math.Ceiling((region.X + region.Width) * scaleX);
            var bottom = (int)Math.Ceiling((region.Y + region.Height) * scaleY);
            regions.Add(new PixelRect(x, y, right - x, bottom - y));
        }

        return regions;
    }
}
