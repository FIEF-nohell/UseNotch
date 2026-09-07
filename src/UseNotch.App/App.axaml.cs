using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using UseNotch.App.Lifecycle;

namespace UseNotch.App;

public partial class App : Avalonia.Application
{
    private static App? _current;
    private static int _activationRequested;
    private static int _shutdownRequested;
    private AppLifecycleCoordinator? _lifecycleCoordinator;

    internal static void RequestActivation()
    {
        Interlocked.Exchange(ref _activationRequested, 1);
        if (Volatile.Read(ref _current) is not null)
        {
            Dispatcher.UIThread.Post(ShowRequestedSettings, DispatcherPriority.Normal);
        }
    }

    internal static void RequestShutdown()
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
        if (Volatile.Read(ref _current) is not null)
        {
            Dispatcher.UIThread.Post(
                () => _current?._lifecycleCoordinator?.Quit(),
                DispatcherPriority.Normal);
        }
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.Exit += OnDesktopExit;
            _current = this;
            _lifecycleCoordinator = new AppLifecycleCoordinator(
                new MainWindowSettingsFactory(),
                () => desktop.Shutdown());

            if (Interlocked.Exchange(ref _shutdownRequested, 0) == 1)
            {
                _lifecycleCoordinator.Quit();
            }
            else if (HasArgument("--show-status") || Interlocked.Exchange(ref _activationRequested, 0) == 1)
            {
                _lifecycleCoordinator.ShowSettings();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowRequestedSettings()
    {
        if (Interlocked.Exchange(ref _activationRequested, 0) == 1)
        {
            _current?._lifecycleCoordinator?.ShowSettings();
        }
    }

    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowSettings();

    private void OnShowSettingsClicked(object? sender, EventArgs e) => ShowSettings();

    private void OnQuitClicked(object? sender, EventArgs e)
    {
        SetTrayVisibility(false);
        _lifecycleCoordinator?.Quit();
    }

    private void ShowSettings() => _lifecycleCoordinator?.ShowSettings();

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        SetTrayVisibility(false);
        _lifecycleCoordinator?.Dispose();
        _lifecycleCoordinator = null;
        _current = null;
    }

    private static bool HasArgument(string expectedArgument) =>
        Environment.GetCommandLineArgs().Any(argument =>
            string.Equals(argument, expectedArgument, StringComparison.OrdinalIgnoreCase));

    private void SetTrayVisibility(bool isVisible)
    {
        foreach (var trayIcon in GetValue(TrayIcon.IconsProperty) ?? [])
        {
            trayIcon.IsVisible = isVisible;
        }
    }
}
