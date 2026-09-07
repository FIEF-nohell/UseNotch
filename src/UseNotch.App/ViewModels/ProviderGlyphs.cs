using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using UseNotch.Domain;

namespace UseNotch.App.ViewModels;

/// <summary>
/// Small monochrome marks that identify each provider inside its ring. They are drawn here rather than
/// shipped as artwork, so no third-party asset is copied into this repository.
/// </summary>
public static class ProviderGlyphs
{
    /// <summary>
    /// A six-lobed rosette for OpenAI, built from six petals around a common centre.
    /// </summary>
    public static Geometry OpenAi { get; } = BuildRosette();

    /// <summary>
    /// An eight-rayed burst for Anthropic, matching the shape their tooling uses.
    /// </summary>
    public static Geometry Anthropic { get; } = BuildBurst();

    public static IValueConverter ToGlyph { get; } = new ProviderGlyphConverter();

    public static Geometry For(ProviderId provider) => provider == ProviderId.OpenAi ? OpenAi : Anthropic;

    private static Geometry BuildRosette()
    {
        const double centre = 50;
        const double orbit = 23;
        const double lobe = 18;
        const double stroke = 4.6;

        // Six overlapping rings rather than six discs: the mark has to read as interlocking loops, and a
        // filled rosette would just look like a blob at 24 device-independent pixels.
        var outer = new GeometryGroup { FillRule = FillRule.NonZero };
        var inner = new GeometryGroup { FillRule = FillRule.NonZero };
        for (var index = 0; index < 6; index++)
        {
            var angle = index * Math.PI / 3;
            var x = centre + (orbit * Math.Cos(angle));
            var y = centre + (orbit * Math.Sin(angle));
            outer.Children.Add(new EllipseGeometry(new Avalonia.Rect(x - lobe, y - lobe, lobe * 2, lobe * 2)));
            inner.Children.Add(new EllipseGeometry(new Avalonia.Rect(
                x - lobe + stroke,
                y - lobe + stroke,
                (lobe - stroke) * 2,
                (lobe - stroke) * 2)));
        }

        return new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
    }

    private static Geometry BuildBurst()
    {
        const double centre = 50;
        const double rays = 12;
        const double tip = 46;
        const double waist = 14;
        const double halfWidth = 4.6;

        // Twelve tapered rays around a small hub, which is how the Claude mark reads at a glance.
        var geometry = new GeometryGroup { FillRule = FillRule.NonZero };
        for (var index = 0; index < rays; index++)
        {
            var angle = index * 2 * Math.PI / rays;
            var ray = new StreamGeometry();
            using (var builder = ray.Open())
            {
                var across = angle + (Math.PI / 2);
                var tipPoint = new Avalonia.Point(centre + (tip * Math.Cos(angle)), centre + (tip * Math.Sin(angle)));
                var leftWaist = new Avalonia.Point(
                    centre + (waist * Math.Cos(angle)) + (halfWidth * Math.Cos(across)),
                    centre + (waist * Math.Sin(angle)) + (halfWidth * Math.Sin(across)));
                var rightWaist = new Avalonia.Point(
                    centre + (waist * Math.Cos(angle)) - (halfWidth * Math.Cos(across)),
                    centre + (waist * Math.Sin(angle)) - (halfWidth * Math.Sin(across)));
                builder.BeginFigure(leftWaist, true);
                builder.LineTo(tipPoint);
                builder.LineTo(rightWaist);
                builder.EndFigure(true);
            }

            geometry.Children.Add(ray);
        }

        geometry.Children.Add(new EllipseGeometry(new Avalonia.Rect(centre - 15, centre - 15, 30, 30)));
        return geometry;
    }

    private sealed class ProviderGlyphConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => For(value is ProviderId provider ? provider : ProviderId.OpenAi);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
