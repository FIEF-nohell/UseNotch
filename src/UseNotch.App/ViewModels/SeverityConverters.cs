using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using UseNotch.Application;

namespace UseNotch.App.ViewModels;

/// <summary>
/// Severity colours for the quota ring. These communicate how close a window is to its limit; provider
/// identity is carried by the label and the automation name, so colour is never the only signal.
/// </summary>
public static class SeverityConverters
{
    // Rings and bars both read as usage: green, then yellow, then red. The ring used to carry each
    // provider's brand accent while the reading was unremarkable, which meant the same colour said
    // "this is Claude" in one state and "this is fine" in another. The provider mark inside the ring
    // already says which provider it is, so colour is free to mean one thing only.
    public static IBrush NormalBrush { get; } = new SolidColorBrush(Color.Parse("#3BE08C"));

    public static IBrush CautionBrush { get; } = new SolidColorBrush(Color.Parse("#FFD24A"));

    public static IBrush ExhaustedBrush { get; } = new SolidColorBrush(Color.Parse("#FF5C4D"));

    public static IBrush UnavailableBrush { get; } = new SolidColorBrush(Color.Parse("#6E7A8C"));

    public static IValueConverter ToArcBrush { get; } = new SeverityBrushConverter();

    /// <summary>
    /// Turns a used fraction into the width of a progress bar's filled part. The detail panel's bars are
    /// a fixed width, so the fraction maps directly onto it.
    /// </summary>
    public static IValueConverter ToBarWidth { get; } = new BarWidthConverter();

    public const double BarWidth = 264;

    public static IBrush BrushFor(QuotaSeverity severity) => severity switch
    {
        QuotaSeverity.Normal => NormalBrush,
        QuotaSeverity.Caution => CautionBrush,
        QuotaSeverity.Exhausted => ExhaustedBrush,
        _ => UnavailableBrush,
    };


    private sealed class BarWidthConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is double fraction ? Math.Clamp(fraction, 0, 1) * BarWidth : 0d;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class SeverityBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => BrushFor(value is QuotaSeverity severity ? severity : QuotaSeverity.Unavailable);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
