using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UseNotch.App.Controls;

/// <summary>
/// The notch silhouette: a slab flush against the screen edge whose inner corners are rounded outward
/// and whose outer corners curve back into the edge itself.
/// <para>
/// A plain rounded rectangle leaves a hard step where it meets the edge. The concave fillets above and
/// below are what make it read as cut into the screen rather than pasted on top of it, so they are drawn
/// as part of one continuous outline.
/// </para>
/// </summary>
public sealed class NotchSurface : Decorator
{
    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty =
        AvaloniaProperty.Register<NotchSurface, IBrush?>(nameof(SurfaceBrush), Brushes.Black);

    /// <summary>Rounding of the two corners facing away from the screen edge.</summary>
    public static readonly StyledProperty<double> InnerRadiusProperty =
        AvaloniaProperty.Register<NotchSurface, double>(nameof(InnerRadius), 26);

    /// <summary>How far the concave fillets reach along the screen edge.</summary>
    public static readonly StyledProperty<double> EdgeRadiusProperty =
        AvaloniaProperty.Register<NotchSurface, double>(nameof(EdgeRadius), 24);

    static NotchSurface()
    {
        AffectsRender<NotchSurface>(SurfaceBrushProperty, InnerRadiusProperty, EdgeRadiusProperty);
        AffectsMeasure<NotchSurface>(EdgeRadiusProperty);
    }

    public IBrush? SurfaceBrush
    {
        get => GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    public double InnerRadius
    {
        get => GetValue(InnerRadiusProperty);
        set => SetValue(InnerRadiusProperty, value);
    }

    public double EdgeRadius
    {
        get => GetValue(EdgeRadiusProperty);
        set => SetValue(EdgeRadiusProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The fillets live above and below the slab, so the control reserves room for them on top of
        // whatever the content needs.
        var reserved = EdgeRadius * 2;
        var padding = Padding;
        var childAvailable = new Size(
            Math.Max(0, availableSize.Width - padding.Left - padding.Right),
            Math.Max(0, availableSize.Height - padding.Top - padding.Bottom - reserved));

        var childSize = default(Size);
        if (Child is { } child)
        {
            child.Measure(childAvailable);
            childSize = child.DesiredSize;
        }

        return new Size(
            childSize.Width + padding.Left + padding.Right,
            childSize.Height + padding.Top + padding.Bottom + reserved);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var padding = Padding;
        Child?.Arrange(new Rect(
            padding.Left,
            EdgeRadius + padding.Top,
            Math.Max(0, finalSize.Width - padding.Left - padding.Right),
            Math.Max(0, finalSize.Height - padding.Top - padding.Bottom - (EdgeRadius * 2))));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        if (SurfaceBrush is not { } brush || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        context.DrawGeometry(brush, null, BuildSilhouette(Bounds.Width, Bounds.Height, InnerRadius, EdgeRadius));
    }

    /// <summary>
    /// Builds the outline for a notch docked against the right edge. Every curve is a quadratic whose
    /// control point sits on the corner it turns, which is what makes the outer pair read as concave and
    /// the inner pair as convex.
    /// </summary>
    internal static Geometry BuildSilhouette(double width, double height, double innerRadius, double edgeRadius)
    {
        var edge = Math.Max(0, Math.Min(edgeRadius, height / 2));
        var inner = Math.Max(0, Math.Min(innerRadius, Math.Min(width, (height - (edge * 2)) / 2)));
        var top = edge;
        var bottom = height - edge;
        var left = 0.0;
        var right = width;

        var geometry = new StreamGeometry();
        using (var builder = geometry.Open())
        {
            builder.BeginFigure(new Point(right, 0), true);

            // Concave turn from the screen edge into the top of the slab.
            builder.QuadraticBezierTo(new Point(right, top), new Point(right - edge, top));
            builder.LineTo(new Point(left + inner, top));

            // Convex top corner facing away from the edge.
            builder.QuadraticBezierTo(new Point(left, top), new Point(left, top + inner));
            builder.LineTo(new Point(left, bottom - inner));

            // Convex bottom corner.
            builder.QuadraticBezierTo(new Point(left, bottom), new Point(left + inner, bottom));
            builder.LineTo(new Point(right - edge, bottom));

            // Concave turn back out to the screen edge.
            builder.QuadraticBezierTo(new Point(right, bottom), new Point(right, height));
            builder.EndFigure(true);
        }

        return geometry;
    }
}
