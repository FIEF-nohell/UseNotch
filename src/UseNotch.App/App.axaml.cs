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
using UseNotch.Infrastructure;
using UseNotch.Platform.Windows.Overlay;
using UseNotch.Platform.Windows.Security;
using UseNotch.Platform.Windows.Session;
using UseNotch.Platform.Windows.Startup;
using UseNotch.Providers.Anthropic;
using UseNotch.Providers.OpenAI;
using PlatformOverlayEdge = UseNotch.Platform.Windows.Overlay.OverlayEdge;

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
    private readonly InMemoryUsageStateStore _store = new();
    private readonly JsonSettingsRepository _settingsRepository = new();
    private readonly RegistryStartupRegistration _startupRegistration = new();
    private readonly SanitizedDiagnosticLog _diagnostics = new();
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
                new MainWindowSettingsFactory(CreateSettingsViewModel),
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
                _overlayViewModel.Apply(OverlayTrigger.Show);
                if (HasArgument("--overlay-smoke-negative"))
                {
                    _overlayController.PreferredMonitorId = new Win32MonitorService()
                        .GetMonitors()
                        .FirstOrDefault(monitor => monitor.Bounds.X < 0)
                        ?.Id;
                }

                _overlayController.Show();
            }

            _ = StartFromSettingsAsync();
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

    private SettingsViewModel CreateSettingsViewModel() => new(
        _settingsRepository,
        _startupRegistration,
        new AppSettingsRuntime(_store, _pollingCoordinator, _activityCoordinator, ApplyOverlaySettings, ApplyAlertThresholds));

    private void ApplyAlertThresholds(AlertThresholds thresholds) => _overlayViewModel.Thresholds = thresholds.ToSeverityThresholds();

    private void ApplyOverlaySettings(OverlaySettings settings)
    {
        if (_overlayController is null || settings is null)
        {
            return;
        }

        _overlayController.PreferredMonitorId = settings.MonitorId;
        _overlayController.Edge = ToPlatformEdge(settings.Edge);
        _overlayViewModel.ReducedMotion = settings.ReducedMotion;
        _overlayViewModel.UiScale = settings.UiScale;
        if (settings.Pinned != _overlayViewModel.Presentation.Pinned)
        {
            _overlayViewModel.Apply(OverlayTrigger.PinToggled);
        }

        if (settings.Visible)
        {
            _overlayController.Show();
            _overlayViewModel.Apply(OverlayTrigger.Show);
        }
        else
        {
            _overlayViewModel.Apply(OverlayTrigger.Hide);
            _overlayController.Hide();
        }
    }

    /// <summary>
    /// The settings edge and the native placement edge are separate types on purpose: the application
    /// layer must not depend on the Windows platform project. They are mapped by name, not by value.
    /// </summary>
    private static PlatformOverlayEdge ToPlatformEdge(UseNotch.Application.OverlayEdge edge) => edge switch
    {
        UseNotch.Application.OverlayEdge.Left => PlatformOverlayEdge.Left,
        UseNotch.Application.OverlayEdge.Top => PlatformOverlayEdge.Top,
        UseNotch.Application.OverlayEdge.Bottom => PlatformOverlayEdge.Bottom,
        _ => PlatformOverlayEdge.Right,
    };

    private static bool HasArgument(string expectedArgument) =>
        Environment.GetCommandLineArgs().Any(argument =>
            string.Equals(argument, expectedArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Saved settings decide what runs. The command-line switches remain available so a development run
    /// can force a provider on without changing the user's stored configuration.
    /// </summary>
    private async Task StartFromSettingsAsync()
    {
        // App-owned secret storage is created with inheritance removed before anything can write to it.
        OwnedDataDirectory.CreateRestricted(ApplicationPaths.Secrets);

        var loaded = await _settingsRepository.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        var settings = loaded.Settings;
        _diagnostics.IsEnabled = settings.Privacy.DiagnosticsEnabled;

        var enableCodex = settings.OpenAi.Enabled || HasArgument("--enable-codex");
        var enableClaude = settings.Anthropic.Enabled || HasArgument("--enable-claude");
        if (enableCodex || enableClaude)
        {
            StartProviderPolling(enableCodex, enableClaude, settings.Privacy.ActivityMonitoringEnabled);
        }

        // Placement and visibility belong to the overlay, not to whether a provider happens to be
        // enabled. Disabling both providers must not silently leave the overlay wherever it started.
        ApplyOverlaySettings(settings.Overlay);
        ApplyAlertThresholds(settings.Alerts);
    }

    private void StartProviderPolling(bool enableCodex, bool enableClaude, bool enableActivity)
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
            if (enableActivity)
            {
                monitors.Add(new CodexActivityMonitor());
            }
        }

        if (enableClaude)
        {
            providers.Add(new ClaudeUsageProvider());
            if (enableActivity)
            {
                monitors.Add(new ClaudeActivityMonitor());
            }
        }

        _pollingCoordinator = new PollingCoordinator(
            providers,
            _store,
            cache: new JsonUsageCache(cacheRoot),
            onPublished: state => Dispatcher.UIThread.Post(() => _overlayViewModel.ApplyRuntimeState(state)));

        // Activity runs in its own coordinator with its own workers, so an unsupported or failing activity
        // source can never delay or break a quota reading.
        _activityCoordinator = new ActivityCoordinator(
            monitors,
            _store,
            onPublished: provider =>
            {
                if (_store.Get(provider) is { } current)
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
            if (enableActivity)
            {
                _activityCoordinator.Start(ProviderId.OpenAi);
            }
        }

        if (enableClaude)
        {
            _ = _pollingCoordinator.StartAsync(new ProviderConnection(ProviderId.Anthropic, "claude:default", true, 1));
            if (enableActivity)
            {
                _activityCoordinator.Start(ProviderId.Anthropic);
            }
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
