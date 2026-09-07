using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.App.ViewModels;

public partial class ProviderCellViewModel(ProviderId provider, string displayName) : ObservableObject
{
    public ProviderId Provider => provider;

    public string DisplayName => displayName;

    public string ShortName { get; } = provider == ProviderId.OpenAi ? "OPENAI" : "ANTHROPIC";

    [ObservableProperty]
    private string _headline = "-";

    [ObservableProperty]
    private string _scopeText = "Reading usage";

    [ObservableProperty]
    private string _statusText = "Loading";

    [ObservableProperty]
    private string _freshnessText = "No successful reading yet";

    [ObservableProperty]
    private string _activityText = "Activity unavailable";

    [ObservableProperty]
    private double? _ringFraction;

    [ObservableProperty]
    private QuotaSeverity _severity = QuotaSeverity.Unavailable;

    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    private bool _isWaiting;

    [ObservableProperty]
    private string _automationName = "No reading";

    [ObservableProperty]
    private string _detailText = "No reading yet.";

    /// <summary>
    /// Every compact number is restated here with the window it belongs to, so nothing shown in the
    /// collapsed surface is left without a named meaning.
    /// </summary>
    public void Apply(QuotaDisplay display, string statusText, ActivityReading? activity, ProviderRuntimeState? state)
    {
        Headline = display.ValueText;
        ScopeText = display.ScopeText;
        FreshnessText = display.FreshnessText;
        RingFraction = display.RingFraction;
        Severity = display.Severity;
        StatusText = statusText;
        AutomationName = display.AutomationName;
        ActivityText = OverlayViewModel.DescribeActivity(activity);
        IsWorking = activity?.Session?.State == ActivityState.Working;
        IsWaiting = activity?.Session?.State == ActivityState.Waiting;
        DetailText = BuildDetail(display, statusText, state);
    }

    private string BuildDetail(QuotaDisplay display, string statusText, ProviderRuntimeState? state)
    {
        var lines = new List<string> { $"{DisplayName}: {statusText}." };
        if (state?.Snapshot is { } snapshot)
        {
            foreach (var window in snapshot.Windows)
            {
                var used = window.Limit.UsedFraction is { } fraction
                    ? string.Create(CultureInfo.InvariantCulture, $"{fraction * 100:0}% used, {Math.Max(0, 100 - (fraction * 100)):0}% remaining")
                    : "value unavailable";
                var reset = window.ResetsAt is { } resets
                    ? ", resets " + resets.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)
                    : string.Empty;
                lines.Add($"{window.Scope}: {used}{reset}.");
            }
        }
        else
        {
            lines.Add($"{display.ScopeText}: {display.ValueText}.");
        }

        if (display.IsOverLimit)
        {
            lines.Add("This window is over its limit; the ring stops at a full circle.");
        }

        lines.Add($"Last successful reading: {display.FreshnessText}.");
        lines.Add($"Activity: {ActivityText}.");
        return string.Join(Environment.NewLine, lines);
    }
}

public partial class OverlayViewModel : ObservableObject
{
    public static MockScenario? DevelopmentScenario { get; set; }

    private readonly TimeProvider _clock;

    public OverlayViewModel(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        OpenAi = new ProviderCellViewModel(ProviderId.OpenAi, "OpenAI / Codex");
        Anthropic = new ProviderCellViewModel(ProviderId.Anthropic, "Anthropic / Claude Code");
    }

    public ProviderCellViewModel OpenAi { get; }

    public ProviderCellViewModel Anthropic { get; }

    [ObservableProperty]
    private OverlayPresentation _presentation = OverlayPresentation.Hidden;

    [ObservableProperty]
    private bool _reducedMotion;

    [ObservableProperty]
    private double _uiScale = 1.0;

    [ObservableProperty]
    private string _detailTitle = "OpenAI / Codex status";

    [ObservableProperty]
    private string _detailText = "No reading yet.";

    public bool ShowsProviderCells => Presentation.ShowsProviderCells;

    public bool ShowsDetails => Presentation.ShowsDetails;

    // Busy motion runs only while fresh activity evidence exists and the user has not asked for less.
    public bool ShowsBusyMotion => !ReducedMotion && Presentation.IsVisible && (OpenAi.IsWorking || Anthropic.IsWorking);

    public OverlayPresentation Apply(OverlayTrigger trigger)
    {
        Presentation = Presentation.Apply(trigger);
        return Presentation;
    }

    public void LoadDevelopmentScenario()
    {
        if (DevelopmentScenario is not { } scenario)
        {
            return;
        }

        ApplyScenario(ProviderId.OpenAi, scenario);
        ApplyScenario(ProviderId.Anthropic, scenario);
    }

    public void ApplyScenario(ProviderId provider, MockScenario scenario)
    {
        var mock = MockScenarioCatalog.Create(provider, scenario, _clock);
        var state = new ProviderRuntimeState(
            new ProviderConnection(provider, "mock", true, 1),
            mock.Snapshot,
            mock.Status,
            0,
            0)
        {
            Activity = mock.Activity is null
                ? null
                : ActivityReading.Supported(provider, _clock.GetUtcNow(), mock.Activity, 1),
        };
        ApplyRuntimeState(state, scenario == MockScenario.Loading ? "Loading" : null);
    }

    public void ApplyRuntimeState(ProviderRuntimeState state) => ApplyRuntimeState(state, null);

    private void ApplyRuntimeState(ProviderRuntimeState state, string? statusOverride)
    {
        var cell = state.Connection.Provider == ProviderId.OpenAi ? OpenAi : Anthropic;
        var display = QuotaDisplay.From(cell.DisplayName, state, _clock.GetUtcNow());
        cell.Apply(display, statusOverride ?? DescribeStatus(state), state.Activity, state);
        OnPropertyChanged(nameof(ShowsBusyMotion));
    }

    public void ShowDetail(ProviderId provider)
    {
        var cell = provider == ProviderId.OpenAi ? OpenAi : Anthropic;
        DetailTitle = cell.DisplayName + " status";
        DetailText = cell.DetailText;
        Apply(OverlayTrigger.ProviderActivated);
    }

    public void HideDetail() => Apply(OverlayTrigger.DetailsClosed);

    public static string DescribeStatus(ProviderRuntimeState state) => state.Status.Authentication switch
    {
        AuthenticationState.Authenticated => state.Status.Freshness switch
        {
            DataFreshness.Fresh => "Connected",
            DataFreshness.Stale => "Stale",
            DataFreshness.Expired => "Expired reading",
            _ => "Reading unavailable",
        },
        AuthenticationState.Missing => "Sign in required",
        AuthenticationState.Expired => "Session expired",
        AuthenticationState.Rejected => "Access rejected",
        AuthenticationState.AccessDenied => "Access denied",
        AuthenticationState.Unsupported => "Unsupported",
        AuthenticationState.Disabled => "Disabled",
        _ => state.Status.Error?.SafeMessage ?? "Loading",
    };

    /// <summary>
    /// Activity is described separately from quota, and an estimate always says so. An unsupported or
    /// unknown source reads as unavailable rather than as an idle provider.
    /// </summary>
    public static string DescribeActivity(ActivityReading? reading) => reading switch
    {
        null => "Activity unavailable",
        { Capability: ActivityCapability.Unsupported } => "Activity unavailable",
        { Session: null } => "No recent activity observed",
        { Session.State: ActivityState.Unknown } => "Activity unknown",
        { Session: { Fidelity: ReadingFidelity.Derived } session } => $"{session.State}, estimated",
        { Session: { } session } => session.State.ToString(),
    };

    partial void OnPresentationChanged(OverlayPresentation value)
    {
        OnPropertyChanged(nameof(ShowsProviderCells));
        OnPropertyChanged(nameof(ShowsDetails));
        OnPropertyChanged(nameof(ShowsBusyMotion));
    }

    partial void OnReducedMotionChanged(bool value) => OnPropertyChanged(nameof(ShowsBusyMotion));
}
