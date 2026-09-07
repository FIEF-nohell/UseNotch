using Avalonia;
using UseNotch.Platform.Windows.SingleInstance;

namespace UseNotch.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var requestQuit = args.Any(argument =>
            string.Equals(argument, "--smoke-quit", StringComparison.OrdinalIgnoreCase));
        using var instanceLease = SingleInstanceLease.AcquireOrSignalExisting(
            App.RequestActivation,
            App.RequestShutdown,
            requestQuit);
        if (instanceLease is null)
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(
            args,
            lifetime => lifetime.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
