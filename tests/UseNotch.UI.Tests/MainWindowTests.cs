using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using UseNotch.App.ViewModels;
using UseNotch.App.Views;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.UI.Tests;

internal sealed class FakeSettingsRepository(UseNotchSettings? initial = null, SettingsLoadOutcome outcome = SettingsLoadOutcome.Loaded) : ISettingsRepository
{
    public UseNotchSettings? Saved { get; private set; }

    public int Saves { get; private set; }

    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(new SettingsLoadResult(initial ?? UseNotchSettings.Default, outcome, null));

    public Task SaveAsync(UseNotchSettings settings, CancellationToken cancellationToken)
    {
        Saved = settings;
        Saves++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeStartupRegistration(StartupRegistrationState initial = StartupRegistrationState.NotRegistered, bool succeeds = true) : IStartupRegistration
{
    private StartupRegistrationState _state = initial;

    public StartupRegistrationState Read() => _state;

    public bool TryRegister()
    {
        if (!succeeds)
        {
            return false;
        }

        _state = StartupRegistrationState.RegisteredForThisApplication;
        return true;
    }

    public bool TryUnregister()
    {
        if (!succeeds)
        {
            return false;
        }

        _state = StartupRegistrationState.NotRegistered;
        return true;
    }
}

internal sealed class FakeSettingsRuntime : ISettingsRuntime
{
    public Dictionary<ProviderId, ProviderRuntimeState> States { get; } = [];

    public List<ProviderId> Refreshes { get; } = [];

    public List<(ProviderId Provider, bool Enabled)> EnableCalls { get; } = [];

    public List<bool> PauseCalls { get; } = [];

    public int ClearCalls { get; private set; }

    public OverlaySettings? AppliedOverlay { get; private set; }

    public ProviderRuntimeState? GetState(ProviderId provider) => States.GetValueOrDefault(provider);

    public string DescribeSource(ProviderId provider) => provider + " source: C:\\fixture";

    public void RequestRefresh(ProviderId provider) => Refreshes.Add(provider);

    public void SetPaused(bool paused) => PauseCalls.Add(paused);

    public Task SetProviderEnabledAsync(ProviderId provider, bool enabled)
    {
        EnableCalls.Add((provider, enabled));
        return Task.CompletedTask;
    }

    public Task ReconnectAsync(ProviderId provider)
    {
        EnableCalls.Add((provider, false));
        EnableCalls.Add((provider, true));
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> ClearOwnedData()
    {
        ClearCalls++;
        return ["cache"];
    }

    public string DescribeDataLocation() => @"C:\fixture\UseNotch";

    public void ApplyOverlaySettings(OverlaySettings settings) => AppliedOverlay = settings;
}

public class MainWindowTests
{
    private static SettingsViewModel CreateViewModel(
        ISettingsRepository? repository = null,
        IStartupRegistration? startup = null,
        ISettingsRuntime? runtime = null,
        TimeProvider? clock = null)
        => new(repository ?? new FakeSettingsRepository(), startup ?? new FakeStartupRegistration(), runtime ?? new FakeSettingsRuntime(), clock: clock);

    [AvaloniaFact]
    public void Settings_window_loads_compiled_XAML_and_updates_from_generated_MVVM_property()
    {
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };

        try
        {
            window.Show();
            var status = window.FindControl<TextBlock>("StatusText");
            Assert.NotNull(status);
            Assert.Equal(viewModel.StatusMessage, status.Text);

            viewModel.StatusMessage = "A fixture update reached the view.";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(viewModel.StatusMessage, status.Text);
        }
        finally
        {
            window.CloseForShutdown();
        }
    }

    [AvaloniaFact]
    public void Ordinary_close_hides_the_settings_window_until_app_shutdown()
    {
        var window = new MainWindow { DataContext = CreateViewModel() };

        window.Show();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);

        window.CloseForShutdown();
    }

    [AvaloniaFact]
    public void Every_documented_navigation_section_is_offered()
    {
        var viewModel = CreateViewModel();

        Assert.Equal(
            [SettingsSection.Status, SettingsSection.Providers, SettingsSection.Appearance, SettingsSection.General, SettingsSection.Privacy, SettingsSection.About],
            viewModel.Sections.Select(item => item.Section));

        // Each entry names itself and says what it is for, so the heading is not just an enum name.
        Assert.All(viewModel.Sections, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Title));
            Assert.False(string.IsNullOrWhiteSpace(item.Description));
        });
    }

    [AvaloniaFact]
    public void Exactly_one_navigation_section_is_marked_selected()
    {
        var viewModel = CreateViewModel();

        Assert.Equal(SettingsSection.Status, Assert.Single(viewModel.Sections, item => item.IsSelected).Section);
        Assert.Equal(SettingsSection.Status, viewModel.CurrentSection.Section);

        viewModel.SelectSectionCommand.Execute(SettingsSection.Privacy);

        // The previous entry must clear, otherwise the navigation shows two open sections at once.
        Assert.Equal(SettingsSection.Privacy, Assert.Single(viewModel.Sections, item => item.IsSelected).Section);
        Assert.Equal("Privacy", viewModel.CurrentSection.Title);
    }

    [AvaloniaFact]
    public async Task Valid_settings_survive_a_restart()
    {
        var repository = new FakeSettingsRepository();
        var viewModel = CreateViewModel(repository);
        await viewModel.LoadAsync();

        viewModel.Edge = OverlayEdge.Left;
        viewModel.OffsetX = 24;
        viewModel.UiScale = 1.5;
        viewModel.ReducedMotion = true;
        await viewModel.PersistAsync();

        var restored = CreateViewModel(new FakeSettingsRepository(repository.Saved));
        await restored.LoadAsync();

        Assert.Equal(OverlayEdge.Left, restored.Edge);
        Assert.Equal(24, restored.OffsetX);
        Assert.Equal(1.5, restored.UiScale);
        Assert.True(restored.ReducedMotion);
    }

    [AvaloniaFact]
    public async Task An_out_of_range_scale_is_clamped_rather_than_persisted()
    {
        var repository = new FakeSettingsRepository();
        var viewModel = CreateViewModel(repository);
        await viewModel.LoadAsync();

        viewModel.UiScale = 25;
        await viewModel.PersistAsync();

        Assert.Equal(OverlaySettings.MaximumScale, repository.Saved!.Overlay.UiScale);
    }

    [AvaloniaFact]
    public async Task Launch_at_login_reports_the_registration_that_actually_exists()
    {
        var startup = new FakeStartupRegistration(succeeds: false);
        var viewModel = CreateViewModel(startup: startup);
        await viewModel.LoadAsync();

        await viewModel.ToggleLaunchAtLoginCommand.ExecuteAsync(null);

        Assert.False(viewModel.LaunchAtLogin);
        Assert.Equal("Not registered", viewModel.LaunchAtLoginStatus);
        Assert.Contains("could not be changed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Launch_at_login_registers_when_the_registration_succeeds()
    {
        var viewModel = CreateViewModel(startup: new FakeStartupRegistration());
        await viewModel.LoadAsync();

        await viewModel.ToggleLaunchAtLoginCommand.ExecuteAsync(null);

        Assert.True(viewModel.LaunchAtLogin);
        Assert.Equal("Registered for this installation", viewModel.LaunchAtLoginStatus);
    }

    [AvaloniaFact]
    public async Task A_registration_pointing_somewhere_else_is_not_shown_as_this_installation()
    {
        var viewModel = CreateViewModel(startup: new FakeStartupRegistration(StartupRegistrationState.RegisteredElsewhere));
        await viewModel.LoadAsync();

        Assert.False(viewModel.LaunchAtLogin);
        Assert.Contains("different UseNotch location", viewModel.LaunchAtLoginStatus, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Pausing_stops_monitoring_and_says_so()
    {
        var runtime = new FakeSettingsRuntime();
        var viewModel = CreateViewModel(runtime: runtime);
        await viewModel.LoadAsync();

        viewModel.TogglePauseCommand.Execute(null);

        Assert.Equal([true], runtime.PauseCalls);
        Assert.Contains("Paused", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Clearing_data_reports_that_provider_installations_are_untouched()
    {
        var runtime = new FakeSettingsRuntime();
        var viewModel = CreateViewModel(runtime: runtime);
        await viewModel.LoadAsync();

        await viewModel.ClearDataCommand.ExecuteAsync(null);

        Assert.Equal(1, runtime.ClearCalls);
        Assert.Contains("never touched", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Disabling_a_provider_disconnects_it_and_stops_showing_a_reading()
    {
        var runtime = new FakeSettingsRuntime();
        var viewModel = CreateViewModel(runtime: runtime);
        await viewModel.LoadAsync();
        var provider = viewModel.Providers.First(candidate => candidate.Provider == ProviderId.OpenAi);

        await provider.ToggleEnabledCommand.ExecuteAsync(null);

        Assert.Contains((ProviderId.OpenAi, false), runtime.EnableCalls);
        Assert.Equal("Disabled", provider.AuthenticationText);
        Assert.Equal("Enable", provider.EnabledLabel);
    }

    [AvaloniaFact]
    public async Task Manual_refresh_is_rate_limited_and_labels_the_wait()
    {
        var runtime = new FakeSettingsRuntime();
        var clock = new FixedTimeProvider(new DateTimeOffset(2027, 1, 15, 12, 0, 0, TimeSpan.Zero));
        var viewModel = CreateViewModel(runtime: runtime, clock: clock);
        await viewModel.LoadAsync();
        var provider = viewModel.Providers.First(candidate => candidate.Provider == ProviderId.Anthropic);

        provider.RefreshNowCommand.Execute(null);
        provider.RefreshNowCommand.Execute(null);

        Assert.Single(runtime.Refreshes);
        Assert.Contains("Refresh available in", provider.RefreshCommandLabel, StringComparison.Ordinal);

        clock.Advance(ProviderSettingsViewModel.MinimumManualRefreshInterval);
        provider.RefreshNowCommand.Execute(null);

        Assert.Equal(2, runtime.Refreshes.Count);
    }

    [AvaloniaFact]
    public async Task Recovery_guidance_points_at_the_owning_tool_and_never_at_a_local_action()
    {
        var runtime = new FakeSettingsRuntime();
        var now = DateTimeOffset.UtcNow;
        runtime.States[ProviderId.Anthropic] = new ProviderRuntimeState(
            new ProviderConnection(ProviderId.Anthropic, "test", true, 1),
            null,
            new ProviderStatus(AuthenticationState.Expired, DataFreshness.Unknown, now, null, null, false, null),
            0,
            0);
        var viewModel = CreateViewModel(runtime: runtime);
        await viewModel.LoadAsync();

        var provider = viewModel.Providers.First(candidate => candidate.Provider == ProviderId.Anthropic);

        Assert.Equal("Session expired in the owning tool", provider.AuthenticationText);
        Assert.Contains("provider's own tool", provider.RecoveryAction, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_resolved_credential_source_is_shown_for_each_provider()
    {
        var viewModel = CreateViewModel();
        await viewModel.LoadAsync();

        Assert.All(viewModel.Providers, provider => Assert.Contains("source:", provider.SourceDescription, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Recovered_settings_say_the_previous_document_was_kept()
    {
        var viewModel = CreateViewModel(new FakeSettingsRepository(outcome: SettingsLoadOutcome.Recovered));

        await viewModel.LoadAsync();

        Assert.Contains("could not be read", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
