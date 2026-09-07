using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.App.ViewModels;

/// <summary>
/// One quota window as the detail panel presents it: what it covers, when it resets, and how full it is.
/// </summary>
public sealed record QuotaWindowRow(string Label, string ResetText, string UsedText, double Fraction, QuotaSeverity Severity);

public partial class ProviderCellViewModel(ProviderId provider, string displayName) : ObservableObject
{
    public ProviderId Provider => provider;

    public string DisplayName => displayName;

    public string DetailTitle { get; } = provider == ProviderId.OpenAi ? "Codex Usage" : "Claude Usage";

    [ObservableProperty]
    private string _percentText = "-";

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

    /// <summary>
    /// Every window the provider reported, so the compact percentage on the ring is never the only place
    /// a number appears without the window it belongs to.
    /// </summary>
    public ObservableCollection<QuotaWindowRow> Windows { get; } = [];

    public void Apply(QuotaDisplay display, string statusText, ActivityReading? activity, ProviderRuntimeState? state, DateTimeOffset now)
    {
        Headline = display.ValueText;
        PercentText = display.RingFraction is { } fraction
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(fraction * 100):0}%")
            : "-";
        ScopeText = display.ScopeText;
        FreshnessText = display.FreshnessText;
        RingFraction = display.RingFraction;
        Severity = display.Severity;
        StatusText = statusText;
        AutomationName = display.AutomationName;
        ActivityText = OverlayViewModel.DescribeActivity(activity);
        IsWorking = activity?.Session?.State == ActivityState.Working;
        IsWaiting = activity?.Session?.State == ActivityState.Waiting;
        RebuildWindows(state, statusText, now);
    }

    private void RebuildWindows(ProviderRuntimeState? state, string statusText, DateTimeOffset now)
    {
        Windows.Clear();
        if (state?.Snapshot is not { } snapshot || snapshot.Windows.Count == 0)
        {
            Windows.Add(new QuotaWindowRow(statusText, string.Empty, FreshnessText, 0, QuotaSeverity.Unavailable));
            return;
        }

        foreach (var window in snapshot.Windows)
        {
            var fraction = window.Limit.UsedFraction;
            var used = fraction is { } value
                ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(value * 100):0}% Used")
                : "Value unavailable";
            Windows.Add(new QuotaWindowRow(
                window.Scope,
                DescribeReset(window.ResetsAt, now),
                used,
                fraction is { } clamped ? Math.Clamp((double)clamped, 0, 1) : 0,
                SeverityFor(fraction)));
        }
    }

    private static QuotaSeverity SeverityFor(decimal? fraction) => fraction switch
    {
        null => QuotaSeverity.Unavailable,
        >= 1m => QuotaSeverity.Exhausted,
        >= (decimal)QuotaDisplay.CautionThreshold => QuotaSeverity.Caution,
        _ => QuotaSeverity.Normal,
    };

    /// <summary>
    /// A near reset reads as a countdown and a distant one as a local time, which is how a person thinks
    /// about "when do I get this back".
    /// </summary>
    private static string DescribeReset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset)
        {
            return string.Empty;
        }

        var remaining = reset - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Resets now";
        }

        if (remaining < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.CurrentCulture, $"Resets in {Math.Max(1, Math.Round(remaining.TotalMinutes)):0} min");
        }

        if (remaining < TimeSpan.FromHours(12))
        {
            return string.Create(CultureInfo.CurrentCulture, $"Resets in {Math.Round(remaining.TotalHours):0} h");
        }

        return "Resets " + reset.ToLocalTime().ToString("ddd h:mm tt", CultureInfo.CurrentCulture);
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
    private ProviderCellViewModel? _detailProvider;

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
        var now = _clock.GetUtcNow();
        var cell = state.Connection.Provider == ProviderId.OpenAi ? OpenAi : Anthropic;
        var display = QuotaDisplay.From(cell.DisplayName, state, now);
        cell.Apply(display, statusOverride ?? DescribeStatus(state), state.Activity, state, now);
        OnPropertyChanged(nameof(ShowsBusyMotion));
    }

    public void ShowDetail(ProviderId provider)
    {
        DetailProvider = provider == ProviderId.OpenAi ? OpenAi : Anthropic;
        Apply(OverlayTrigger.ProviderActivated);
    }

    public void HideDetail()
    {
        Apply(OverlayTrigger.DetailsClosed);
        DetailProvider = null;
    }

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
