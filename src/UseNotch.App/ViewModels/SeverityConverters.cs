using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.App.ViewModels;

/// <summary>
/// Severity colours for the quota ring. These communicate how close a window is to its limit; provider
/// identity is carried by the label and the automation name, so colour is never the only signal.
/// </summary>
public static class SeverityConverters
{
    // Each provider keeps its own accent on its ring, so the two are told apart at a glance. Severity
    // still overrides it once a window is close to or past its limit, which is the thing worth warning
    // about; the detail bars stay severity-coloured throughout.
    //
    // Both accents keep their brand hue but are lifted in saturation and lightness, because the brand
    // values are chosen for white backgrounds and go muddy on a black surface at a four-pixel stroke.
    // OpenAI green 10A37F becomes 1FD69F; Claude clay D97757 becomes FF7A4D.
    public static IBrush OpenAiAccent { get; } = new SolidColorBrush(Color.Parse("#1FD69F"));

    public static IBrush AnthropicAccent { get; } = new SolidColorBrush(Color.Parse("#FF7A4D"));

    public static IBrush NormalBrush { get; } = new SolidColorBrush(Color.Parse("#3BE08C"));

    public static IBrush CautionBrush { get; } = new SolidColorBrush(Color.Parse("#FFD24A"));

    public static IBrush ExhaustedBrush { get; } = new SolidColorBrush(Color.Parse("#FF5C4D"));

    public static IBrush UnavailableBrush { get; } = new SolidColorBrush(Color.Parse("#6E7A8C"));

    public static IValueConverter ToArcBrush { get; } = new SeverityBrushConverter();

    /// <summary>
    /// Ring colour for a provider cell: the provider's own accent while the reading is unremarkable, and
    /// the severity colour once it is worth warning about.
    /// </summary>
    public static IMultiValueConverter ToRingBrush { get; } = new RingBrushConverter();

    public static IBrush AccentFor(ProviderId provider)
        => provider == ProviderId.OpenAi ? OpenAiAccent : AnthropicAccent;

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

    private sealed class RingBrushConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            var severity = values.Count > 0 && values[0] is QuotaSeverity value ? value : QuotaSeverity.Unavailable;
            var provider = values.Count > 1 && values[1] is ProviderId id ? id : ProviderId.OpenAi;
            return severity switch
            {
                QuotaSeverity.Normal => AccentFor(provider),
                QuotaSeverity.Caution => CautionBrush,
                QuotaSeverity.Exhausted => ExhaustedBrush,
                _ => UnavailableBrush,
            };
        }
    }

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
