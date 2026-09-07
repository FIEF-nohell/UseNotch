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

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        // A supported software-rendering fallback for machines where the GPU path misbehaves. It is
        // opt-in, so the normal run keeps hardware rendering.
        if (Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, "--software-render", StringComparison.OrdinalIgnoreCase)))
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
            });
        }

        return builder;
    }
}
