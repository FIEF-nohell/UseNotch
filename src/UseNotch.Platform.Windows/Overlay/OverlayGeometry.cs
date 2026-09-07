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
