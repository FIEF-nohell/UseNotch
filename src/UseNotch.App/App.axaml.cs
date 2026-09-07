using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using UseNotch.App.Lifecycle;
using UseNotch.App.Overlay;
using UseNotch.App.ViewModels;
using UseNotch.App.Views;
using UseNotch.Application;
using UseNotch.Platform.Windows.Overlay;

namespace UseNotch.App;

public partial class App : Avalonia.Application
{
    private static App? _current;
    private static int _activationRequested;
    private static int _shutdownRequested;
    private AppLifecycleCoordinator? _lifecycleCoordinator;
    private OverlayController? _overlayController;

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
            _overlayController = new OverlayController(
                new Win32MonitorService(),
                new Win32OverlayWindowPlatform(),
                () => new OverlayWindow());

            if (Interlocked.Exchange(ref _shutdownRequested, 0) == 1)
            {
                _lifecycleCoordinator.Quit();
            }
            else if (HasArgument("--show-status") || Interlocked.Exchange(ref _activationRequested, 0) == 1)
            {
                _lifecycleCoordinator.ShowSettings();
            }

            var mockScenario = Environment.GetCommandLineArgs()
                .FirstOrDefault(argument => argument.StartsWith("--mock-scenario=", StringComparison.OrdinalIgnoreCase))
                ?.Split('=', 2)[1];
            if (mockScenario is not null && Enum.TryParse<MockScenario>(mockScenario, true, out var scenario))
            {
                OverlayViewModel.DevelopmentScenario = scenario;
            }

            if (HasArgument("--overlay-smoke"))
            {
                if (HasArgument("--overlay-smoke-negative"))
                {
                    _overlayController.PreferredMonitorId = new Win32MonitorService()
                        .GetMonitors()
                        .FirstOrDefault(monitor => monitor.Bounds.X < 0)
                        ?.Id;
                }

                _overlayController.Show();
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

    private void OnToggleOverlayClicked(object? sender, EventArgs e) => _overlayController?.Toggle();

    private void OnQuitClicked(object? sender, EventArgs e)
    {
        SetTrayVisibility(false);
        _lifecycleCoordinator?.Quit();
    }

    private void ShowSettings() => _lifecycleCoordinator?.ShowSettings();

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        SetTrayVisibility(false);
        _overlayController?.Dispose();
        _overlayController = null;
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
