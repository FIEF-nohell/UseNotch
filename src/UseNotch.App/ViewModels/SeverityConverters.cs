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
    public static IBrush NormalBrush { get; } = new SolidColorBrush(Color.Parse("#67D6A3"));

    public static IBrush CautionBrush { get; } = new SolidColorBrush(Color.Parse("#F4C66B"));

    public static IBrush ExhaustedBrush { get; } = new SolidColorBrush(Color.Parse("#FF8585"));

    public static IBrush UnavailableBrush { get; } = new SolidColorBrush(Color.Parse("#687588"));

    public static IValueConverter ToArcBrush { get; } = new SeverityBrushConverter();

    public static IBrush BrushFor(QuotaSeverity severity) => severity switch
    {
        QuotaSeverity.Normal => NormalBrush,
        QuotaSeverity.Caution => CautionBrush,
        QuotaSeverity.Exhausted => ExhaustedBrush,
        _ => UnavailableBrush,
    };

    private sealed class SeverityBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => BrushFor(value is QuotaSeverity severity ? severity : QuotaSeverity.Unavailable);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
