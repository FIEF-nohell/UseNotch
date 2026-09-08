using System.Globalization;
using Avalonia.Data.Converters;

namespace UseNotch.App.ViewModels;

/// <summary>
/// Section visibility for the settings navigation. Each converter answers one question so the markup
/// stays declarative and every section's condition is visible in one place.
/// </summary>
public static class SectionConverters
{
    /// <summary>
    /// Status reports what each provider is doing; Providers configures them. They used to render the
    /// same content, which made one of the two navigation entries pointless.
    /// </summary>
    public static IValueConverter IsStatus { get; } = new SectionConverter(SettingsSection.Status);

    public static IValueConverter IsProviders { get; } = new SectionConverter(SettingsSection.Providers);

    public static IValueConverter IsAppearance { get; } = new SectionConverter(SettingsSection.Appearance);

    public static IValueConverter IsGeneral { get; } = new SectionConverter(SettingsSection.General);

    public static IValueConverter IsPrivacy { get; } = new SectionConverter(SettingsSection.Privacy);

    public static IValueConverter IsAbout { get; } = new SectionConverter(SettingsSection.About);

    private sealed class SectionConverter(params SettingsSection[] sections) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is SettingsSection section && sections.Contains(section);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
