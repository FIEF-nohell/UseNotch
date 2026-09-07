using System.Diagnostics;
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
using UseNotch.Domain;
using UseNotch.Platform.Windows.Overlay;
using UseNotch.Platform.Windows.Session;
using UseNotch.Providers.Anthropic;
using UseNotch.Providers.OpenAI;

namespace UseNotch.App;

public partial class App : Avalonia.Application
{
    private static App? _current;
    private static int _activationRequested;
    private static int _shutdownRequested;
    private AppLifecycleCoordinator? _lifecycleCoordinator;
    private OverlayController? _overlayController;
    private PollingCoordinator? _pollingCoordinator;
    private ActivityCoordinator? _activityCoordinator;
    private SessionLockWatcher? _sessionLockWatcher;
    private readonly OverlayViewModel _overlayViewModel = new();
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(5);

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
                () => new OverlayWindow(_overlayViewModel));

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

            var enableCodex = HasArgument("--enable-codex");
            var enableClaude = HasArgument("--enable-claude");
            if (enableCodex || enableClaude)
            {
                StartProviderPolling(enableCodex, enableClaude);
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
        _sessionLockWatcher?.Dispose();
        _sessionLockWatcher = null;
        if (_activityCoordinator is { } activityCoordinator)
        {
            _activityCoordinator = null;
            if (!Task.Run(() => activityCoordinator.DisposeAsync().AsTask()).Wait(ShutdownBudget))
            {
                Trace.TraceWarning("Activity shutdown exceeded its budget; exiting anyway.");
            }
        }

        if (_pollingCoordinator is { } pollingCoordinator)
        {
            _pollingCoordinator = null;
            // Exit runs on the UI thread. Dispose the workers on the thread pool and give up after a
            // bounded wait, so a stuck provider read can delay shutdown but can never prevent it.
            if (!Task.Run(() => pollingCoordinator.DisposeAsync().AsTask()).Wait(ShutdownBudget))
            {
                Trace.TraceWarning("Polling shutdown exceeded its budget; exiting anyway.");
            }
        }

        _overlayController?.Dispose();
        _overlayController = null;
        _lifecycleCoordinator?.Dispose();
        _lifecycleCoordinator = null;
        _current = null;
    }

    private static bool HasArgument(string expectedArgument) =>
        Environment.GetCommandLineArgs().Any(argument =>
            string.Equals(argument, expectedArgument, StringComparison.OrdinalIgnoreCase));

    private void StartProviderPolling(bool enableCodex, bool enableClaude)
    {
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UseNotch",
            "cache");
        var providers = new List<IUsageProvider>();
        var monitors = new List<IActivityMonitor>();
        if (enableCodex)
        {
            providers.Add(new CodexUsageProvider());
            monitors.Add(new CodexActivityMonitor());
        }

        if (enableClaude)
        {
            providers.Add(new ClaudeUsageProvider());
            monitors.Add(new ClaudeActivityMonitor());
        }

        var store = new InMemoryUsageStateStore();
        _pollingCoordinator = new PollingCoordinator(
            providers,
            store,
            cache: new JsonUsageCache(cacheRoot),
            onPublished: state => Dispatcher.UIThread.Post(() => _overlayViewModel.ApplyRuntimeState(state)));

        // Activity runs in its own coordinator with its own workers, so an unsupported or failing activity
        // source can never delay or break a quota reading.
        _activityCoordinator = new ActivityCoordinator(
            monitors,
            store,
            onPublished: provider =>
            {
                if (store.Get(provider) is { } current)
                {
                    Dispatcher.UIThread.Post(() => _overlayViewModel.ApplyRuntimeState(current));
                }
            });

        // Nobody can read the overlay while the session is locked, so stop scanning until it returns.
        _sessionLockWatcher = new SessionLockWatcher(locked =>
        {
            _activityCoordinator?.SetPaused(locked);
            _pollingCoordinator?.SetPaused(locked);
        });

        // Each provider gets its own independent worker, so a failing Claude read cannot stop Codex.
        if (enableCodex)
        {
            _ = _pollingCoordinator.StartAsync(new ProviderConnection(ProviderId.OpenAi, "codex:default", true, 1));
            _activityCoordinator.Start(ProviderId.OpenAi);
        }

        if (enableClaude)
        {
            _ = _pollingCoordinator.StartAsync(new ProviderConnection(ProviderId.Anthropic, "claude:default", true, 1));
            _activityCoordinator.Start(ProviderId.Anthropic);
        }
    }

    private void SetTrayVisibility(bool isVisible)
    {
        foreach (var trayIcon in GetValue(TrayIcon.IconsProperty) ?? [])
        {
            trayIcon.IsVisible = isVisible;
        }
    }
}
