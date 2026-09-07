using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UseNotch.Application;

namespace UseNotch.App.Controls;

/// <summary>
/// A small quota ring built for this application rather than scaled down from a hardware gauge. It is a
/// full 360-degree sweep at a 36 DIP diameter, it draws no arc at all for a zero reading, and it never
/// invents a value: a missing reading shows only the track.
/// </summary>
public sealed class QuotaRing : Control
{
    public static readonly StyledProperty<double?> FractionProperty =
        AvaloniaProperty.Register<QuotaRing, double?>(nameof(Fraction));

    public static readonly StyledProperty<IBrush> TrackBrushProperty =
        AvaloniaProperty.Register<QuotaRing, IBrush>(nameof(TrackBrush), new SolidColorBrush(Color.Parse("#343B46")));

    public static readonly StyledProperty<IBrush> ArcBrushProperty =
        AvaloniaProperty.Register<QuotaRing, IBrush>(nameof(ArcBrush), new SolidColorBrush(Color.Parse("#67D6A3")));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<QuotaRing, double>(nameof(StrokeThickness), 3.0);

    static QuotaRing()
    {
        AffectsRender<QuotaRing>(FractionProperty, TrackBrushProperty, ArcBrushProperty, StrokeThicknessProperty);
    }

    public double? Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public IBrush TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush ArcBrush
    {
        get => GetValue(ArcBrushProperty);
        set => SetValue(ArcBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= StrokeThickness)
        {
            return;
        }

        var radius = (size - StrokeThickness) / 2;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var trackPen = new Pen(TrackBrush, StrokeThickness);
        context.DrawEllipse(null, trackPen, centre, radius, radius);

        var fraction = Fraction;
        if (!QuotaRingGeometry.HasVisibleArc(fraction))
        {
            return;
        }

        var arcPen = new Pen(ArcBrush, StrokeThickness, lineCap: PenLineCap.Round);
        if (QuotaRingGeometry.IsFullCircle(fraction))
        {
            // A full circle and an over-limit reading look the same on purpose. The value text keeps them
            // apart, so the ring never implies more than a completed window.
            context.DrawEllipse(null, arcPen, centre, radius, radius);
            return;
        }

        var start = QuotaRingGeometry.PointAt(centre.X, centre.Y, radius, 0);
        var end = QuotaRingGeometry.PointAt(centre.X, centre.Y, radius, fraction!.Value);
        var geometry = new StreamGeometry();
        using (var builder = geometry.Open())
        {
            builder.BeginFigure(new Point(start.X, start.Y), false);
            builder.ArcTo(
                new Point(end.X, end.Y),
                new Size(radius, radius),
                0,
                QuotaRingGeometry.SweepDegrees(fraction) > 180,
                SweepDirection.Clockwise);
            builder.EndFigure(false);
        }

        context.DrawGeometry(null, arcPen, geometry);
    }

    protected override Size MeasureOverride(Size availableSize) => new(36, 36);
}
