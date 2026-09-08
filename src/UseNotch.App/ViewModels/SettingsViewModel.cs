using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.App.ViewModels;

public enum SettingsSection { Status, Providers, Appearance, General, Privacy, About }

/// <summary>
/// Everything the settings window needs from the running application. Keeping it behind an interface
/// lets the settings behavior be tested without a tray, an overlay, or a provider account.
/// </summary>
public interface ISettingsRuntime
{
    ProviderRuntimeState? GetState(ProviderId provider);

    string DescribeSource(ProviderId provider);

    void RequestRefresh(ProviderId provider);

    void SetPaused(bool paused);

    Task SetProviderEnabledAsync(ProviderId provider, bool enabled);

    Task ReconnectAsync(ProviderId provider);

    IReadOnlyList<string> ClearOwnedData();

    void ApplyOverlaySettings(OverlaySettings settings);
}

public partial class ProviderSettingsViewModel : ObservableObject
{
    public static readonly TimeSpan MinimumManualRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly ISettingsRuntime _runtime;
    private readonly TimeProvider _clock;
    private readonly Func<Task> _persist;
    private DateTimeOffset? _lastManualRefresh;

    public ProviderSettingsViewModel(
        ProviderId provider,
        string displayName,
        ISettingsRuntime runtime,
        TimeProvider clock,
        Func<Task> persist)
    {
        Provider = provider;
        DisplayName = displayName;
        _runtime = runtime;
        _clock = clock;
        _persist = persist;
    }

    public ProviderId Provider { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    private bool _enabled = true;

    public string EnabledLabel => Enabled ? "Disable" : "Enable";

    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(EnabledLabel));

    [ObservableProperty]
    private string? _selectedRoot;

    [ObservableProperty]
    private string _sourceDescription = "Source not resolved yet";

    [ObservableProperty]
    private string _quotaScope = "Quota scope unavailable";

    [ObservableProperty]
    private string _authenticationText = "Discovering";

    [ObservableProperty]
    private string _recoveryAction = "No action needed";

    [ObservableProperty]
    private string _activityText = "Activity unavailable";

    [ObservableProperty]
    private string _freshnessText = "No reading yet";

    [ObservableProperty]
    private string _refreshCommandLabel = "Refresh now";

    public void Refresh()
    {
        SourceDescription = _runtime.DescribeSource(Provider);
        var state = _runtime.GetState(Provider);
        if (!Enabled)
        {
            AuthenticationText = "Disabled";
            QuotaScope = "Not monitored while disabled";
            RecoveryAction = "Enable this provider to resume monitoring";
            ActivityText = "Activity unavailable";
            FreshnessText = "No reading while disabled";
            return;
        }

        if (state is null)
        {
            AuthenticationText = "Discovering";
            QuotaScope = "Quota scope unavailable";
            RecoveryAction = "No action needed";
            ActivityText = "Activity unavailable";
            FreshnessText = "No reading yet";
            return;
        }

        AuthenticationText = Describe(state.Status.Authentication);
        QuotaScope = state.Snapshot is { } snapshot
            ? string.Join(", ", snapshot.Windows.Select(window => window.Scope))
            : "Quota scope unavailable";
        RecoveryAction = DescribeRecovery(state);
        ActivityText = OverlayViewModel.DescribeActivity(state.Activity);
        FreshnessText = DescribeFreshness(state);
    }

    public void ApplySettings(ProviderSettings settings)
    {
        Enabled = settings.Enabled;
        SelectedRoot = settings.SelectedRoot;
        Refresh();
    }

    public ProviderSettings ToSettings() => new(Enabled, SelectedRoot);

    public bool CanRefresh()
        => Enabled && (_lastManualRefresh is not { } last || _clock.GetUtcNow() - last >= MinimumManualRefreshInterval);

    [RelayCommand]
    private void RefreshNow()
    {
        if (!CanRefresh())
        {
            var remaining = MinimumManualRefreshInterval - (_clock.GetUtcNow() - _lastManualRefresh!.Value);
            RefreshCommandLabel = string.Create(CultureInfo.InvariantCulture, $"Refresh available in {Math.Ceiling(remaining.TotalSeconds):0}s");
            return;
        }

        _lastManualRefresh = _clock.GetUtcNow();
        RefreshCommandLabel = "Refresh requested";
        _runtime.RequestRefresh(Provider);
        Refresh();
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync()
    {
        Enabled = !Enabled;
        await _runtime.SetProviderEnabledAsync(Provider, Enabled).ConfigureAwait(true);
        await _persist().ConfigureAwait(true);
        Refresh();
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        await _runtime.ReconnectAsync(Provider).ConfigureAwait(true);
        _lastManualRefresh = null;
        RefreshCommandLabel = "Refresh now";
        Refresh();
    }

    private static string Describe(AuthenticationState state) => state switch
    {
        AuthenticationState.Authenticated => "Connected",
        AuthenticationState.Missing => "Sign-in required in the owning tool",
        AuthenticationState.Expired => "Session expired in the owning tool",
        AuthenticationState.Rejected => "Credential rejected",
        AuthenticationState.AccessDenied => "Access denied",
        AuthenticationState.Unsupported => "Authentication method not supported",
        AuthenticationState.Disabled => "Disabled",
        AuthenticationState.PresentUnverified => "Credential found, not yet verified",
        _ => "Discovering",
    };

    /// <summary>
    /// Recovery guidance always points at the owning tool. UseNotch borrows credentials read-only and
    /// never refreshes, rewrites, or signs anything out on the user's behalf.
    /// </summary>
    private static string DescribeRecovery(ProviderRuntimeState state) => state.Status.Authentication switch
    {
        AuthenticationState.Missing => "Sign in with the provider's own tool, then refresh here",
        AuthenticationState.Expired => "Renew the session in the provider's own tool, then refresh here",
        AuthenticationState.Rejected => "Sign in again with the provider's own tool",
        AuthenticationState.AccessDenied => "This account cannot read usage; check it with the provider",
        AuthenticationState.Unsupported => "This credential storage mode does not expose usage",
        _ => state.Status.Error is { } error
            ? error.Category == ErrorCategory.RateLimited
                ? "Rate limited; the next attempt is already scheduled"
                : "Waiting for the next scheduled attempt"
            : "No action needed",
    };

    private static string DescribeFreshness(ProviderRuntimeState state)
    {
        if (state.Snapshot is null)
        {
            return "No reading yet";
        }

        var origin = state.Origin == StateOrigin.CachedStartup ? "Restored from cache" : "Live reading";
        return string.Create(CultureInfo.InvariantCulture, $"{origin}, {state.Status.Freshness}");
    }
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsRepository _repository;
    private readonly IStartupRegistration _startup;
    private readonly ISettingsRuntime _runtime;
    private readonly TimeProvider _clock;
    private bool _loading;

    public SettingsViewModel(
        ISettingsRepository repository,
        IStartupRegistration startup,
        ISettingsRuntime runtime,
        IProviderRegistry? registry = null,
        TimeProvider? clock = null)
    {
        _repository = repository;
        _startup = startup;
        _runtime = runtime;
        _clock = clock ?? TimeProvider.System;
        var definitions = (registry ?? new ProviderRegistry()).Definitions;
        Providers = new ObservableCollection<ProviderSettingsViewModel>(
            definitions.Select(definition => new ProviderSettingsViewModel(definition.Id, definition.DisplayName, runtime, _clock, PersistAsync)));
        Sections = new ObservableCollection<SettingsSection>(Enum.GetValues<SettingsSection>());
    }

    public ObservableCollection<SettingsSection> Sections { get; }

    public ObservableCollection<ProviderSettingsViewModel> Providers { get; }

    public IReadOnlyList<OverlayEdge> Edges { get; } = Enum.GetValues<OverlayEdge>();

    /// <summary>
    /// Product identity, read from the compiled assembly rather than repeated here. The build defines
    /// these once in Directory.Build.props, so About, Explorer, and the installer cannot disagree.
    /// </summary>
    private static readonly Assembly ProductAssembly = typeof(SettingsViewModel).Assembly;

    public string Version { get; } = ProductAssembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public string ProductName { get; } =
        ProductAssembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "UseNotch";

    public string Tagline { get; } =
        ProductAssembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description
        ?? "Usage overlay for OpenAI Codex and Anthropic Claude Code";

    public string Copyright { get; } =
        ProductAssembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;

    [ObservableProperty]
    private SettingsSection _selectedSection = SettingsSection.Status;

    [ObservableProperty]
    private string? _monitorId;

    [ObservableProperty]
    private OverlayEdge _edge = OverlayEdge.Right;

    [ObservableProperty]
    private int _offsetX;

    [ObservableProperty]
    private int _offsetY;

    [ObservableProperty]
    private bool _pinned;

    [ObservableProperty]
    private bool _overlayVisible = true;

    [ObservableProperty]
    private double _uiScale = 1.0;

    [ObservableProperty]
    private bool _reducedMotion;

    [ObservableProperty]
    private bool _visibleOverFullScreen;

    [ObservableProperty]
    private bool _paused;

    [ObservableProperty]
    private bool _launchAtLogin;

    [ObservableProperty]
    private string _launchAtLoginStatus = "Not registered";

    [ObservableProperty]
    private bool _activityMonitoringEnabled = true;

    [ObservableProperty]
    private bool _diagnosticsEnabled;

    [ObservableProperty]
    private string _statusMessage = "Settings not loaded yet";

    [ObservableProperty]
    private string _dataLocation = string.Empty;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _repository.LoadAsync(cancellationToken).ConfigureAwait(true);
        _loading = true;
        try
        {
            Apply(result.Settings);
            StatusMessage = result.Outcome switch
            {
                SettingsLoadOutcome.Defaults => "Using default settings",
                SettingsLoadOutcome.Migrated => "Settings migrated to the current version",
                SettingsLoadOutcome.Recovered => "Previous settings could not be read and were kept beside the defaults",
                _ => "Settings loaded",
            };
        }
        finally
        {
            _loading = false;
        }

        RefreshStartupStatus();
        RefreshProviders();
    }

    public void Apply(UseNotchSettings settings)
    {
        MonitorId = settings.Overlay.MonitorId;
        Edge = settings.Overlay.Edge;
        OffsetX = settings.Overlay.OffsetX;
        OffsetY = settings.Overlay.OffsetY;
        Pinned = settings.Overlay.Pinned;
        OverlayVisible = settings.Overlay.Visible;
        UiScale = settings.Overlay.UiScale;
        ReducedMotion = settings.Overlay.ReducedMotion;
        VisibleOverFullScreen = settings.Overlay.VisibleOverFullScreen;
        LaunchAtLogin = settings.LaunchAtLogin;
        ActivityMonitoringEnabled = settings.Privacy.ActivityMonitoringEnabled;
        DiagnosticsEnabled = settings.Privacy.DiagnosticsEnabled;
        foreach (var provider in Providers)
        {
            provider.ApplySettings(settings.For(provider.Provider));
        }
    }

    public UseNotchSettings ToSettings() => new(
        UseNotchSettings.CurrentSchemaVersion,
        Providers.First(provider => provider.Provider == ProviderId.OpenAi).ToSettings(),
        Providers.First(provider => provider.Provider == ProviderId.Anthropic).ToSettings(),
        new OverlaySettings(MonitorId, Edge, OffsetX, OffsetY, Pinned, OverlayVisible, UiScale, ReducedMotion, VisibleOverFullScreen),
        new PrivacySettings(ActivityMonitoringEnabled, DiagnosticsEnabled),
        LaunchAtLogin);

    public void RefreshProviders()
    {
        foreach (var provider in Providers)
        {
            provider.Refresh();
        }
    }

    public async Task PersistAsync()
    {
        if (_loading)
        {
            return;
        }

        var settings = SettingsValidator.Normalize(ToSettings());
        await _repository.SaveAsync(settings, CancellationToken.None).ConfigureAwait(true);
        _runtime.ApplyOverlaySettings(settings.Overlay);
    }

    [RelayCommand]
    private void TogglePause()
    {
        Paused = !Paused;
        _runtime.SetPaused(Paused);
        StatusMessage = Paused
            ? "Paused. No provider requests or activity scans are running"
            : "Resumed";
        RefreshProviders();
    }

    [RelayCommand]
    private async Task ToggleLaunchAtLoginAsync()
    {
        var desired = !LaunchAtLogin;
        var succeeded = desired ? _startup.TryRegister() : _startup.TryUnregister();
        RefreshStartupStatus();

        // Report what the registry actually contains, never the value that was merely requested.
        LaunchAtLogin = _startup.Read() == StartupRegistrationState.RegisteredForThisApplication;
        if (!succeeded)
        {
            StatusMessage = "Launch at login could not be changed for this user";
        }

        await PersistAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ClearDataAsync()
    {
        var removed = _runtime.ClearOwnedData();
        StatusMessage = removed.Count == 0
            ? "No UseNotch data needed clearing. Provider installations are never touched"
            : string.Create(CultureInfo.InvariantCulture, $"Cleared {removed.Count} UseNotch data folder(s). Provider installations are never touched");
        await Task.CompletedTask.ConfigureAwait(true);
        RefreshProviders();
    }

    [RelayCommand]
    private void SelectSection(SettingsSection section)
    {
        SelectedSection = section;
        if (section == SettingsSection.Status || section == SettingsSection.Providers)
        {
            RefreshProviders();
        }
    }

    private void RefreshStartupStatus()
    {
        LaunchAtLoginStatus = _startup.Read() switch
        {
            StartupRegistrationState.RegisteredForThisApplication => "Registered for this installation",
            StartupRegistrationState.RegisteredElsewhere => "A different UseNotch location is registered",
            StartupRegistrationState.Unavailable => "Launch at login is unavailable for this user",
            _ => "Not registered",
        };
    }

    partial void OnEdgeChanged(OverlayEdge value) => _ = PersistAsync();

    partial void OnOffsetXChanged(int value) => _ = PersistAsync();

    partial void OnOffsetYChanged(int value) => _ = PersistAsync();

    partial void OnPinnedChanged(bool value) => _ = PersistAsync();

    partial void OnOverlayVisibleChanged(bool value) => _ = PersistAsync();

    partial void OnUiScaleChanged(double value) => _ = PersistAsync();

    partial void OnReducedMotionChanged(bool value) => _ = PersistAsync();

    partial void OnVisibleOverFullScreenChanged(bool value) => _ = PersistAsync();

    partial void OnActivityMonitoringEnabledChanged(bool value) => _ = PersistAsync();

    partial void OnDiagnosticsEnabledChanged(bool value) => _ = PersistAsync();
}
