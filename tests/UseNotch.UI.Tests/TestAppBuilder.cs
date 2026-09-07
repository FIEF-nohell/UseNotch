using Avalonia;
using Avalonia.Headless;
using UseNotch.UI.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace UseNotch.UI.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<UseNotch.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
