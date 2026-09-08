using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.App.ViewModels;

/// <summary>
/// One quota window as the detail panel presents it: what it covers, when it resets, how full it is, and
/// (when there is enough session-local history) how fast it is moving.
/// </summary>
public sealed record QuotaWindowRow(string Label, string ResetText, string UsedText, double Fraction, QuotaSeverity Severity, string? TrendText = null);

public partial class ProviderCellViewModel(ProviderId provider, string displayName, IUsageTrendStore? trendStore = null) : ObservableObject
{
    private readonly IUsageTrendStore _trendStore = trendStore ?? new InMemoryUsageTrendStore();
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

    public void Apply(QuotaDisplay display, string statusText, ActivityReading? activity, ProviderRuntimeState? state, DateTimeOffset now, SeverityThresholds thresholds)
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
        RebuildWindows(state, statusText, now, thresholds);
    }

    private void RebuildWindows(ProviderRuntimeState? state, string statusText, DateTimeOffset now, SeverityThresholds thresholds)
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

            // Recorded whether or not the fraction is usable, so a rollover (which changes ResetsAt) is
            // still detected even while a reading is temporarily unavailable.
            _trendStore.Record(provider, window.Id, window.ResetsAt, new QuotaSample(now, fraction));
            var estimate = _trendStore.Estimate(provider, window.Id, now);
            var trendText = UsageTrendPresenter.Describe(estimate, window.ResetsAt, now);

            Windows.Add(new QuotaWindowRow(
                window.Scope,
                DescribeReset(window.ResetsAt, now),
                used,
                fraction is { } clamped ? Math.Clamp((double)clamped, 0, 1) : 0,
                SeverityFor(fraction, thresholds),
                trendText));
        }
    }

    private static QuotaSeverity SeverityFor(decimal? fraction, SeverityThresholds thresholds) => fraction switch
    {
        null => QuotaSeverity.Unavailable,
        >= 1m => QuotaSeverity.Exhausted,
        _ when fraction >= (decimal)thresholds.Critical => QuotaSeverity.Exhausted,
        _ when fraction >= (decimal)thresholds.Warning => QuotaSeverity.Caution,
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

    /// <summary>
    /// One line describing this provider's usage, built only from what the detail panel already shows:
    /// the display name and each window's label, used text, and reset text. Never the account partition
    /// or any credential/session data, so it is safe to paste anywhere.
    /// </summary>
    public string BuildCopySummary()
    {
        if (Windows.Count == 0)
        {
            return DisplayName;
        }

        var segments = Windows.Select(window => string.IsNullOrEmpty(window.ResetText)
            ? $"{window.Label} {window.UsedText}"
            : $"{window.Label} {window.UsedText} ({window.ResetText})");
        return $"{DisplayName} - {string.Join(", ", segments)}";
    }
}

/// <summary>
/// A transient, dismissible message that a provider's reading just worsened into a new severity band.
/// Session-local, like the crossing detector that produces it.
/// </summary>
public sealed record NoticeViewModel(ProviderId Provider, QuotaSeverity Severity, string Message);

public partial class OverlayViewModel : ObservableObject
{
    public static MockScenario? DevelopmentScenario { get; set; }

    // Long enough to read a short sentence without rushing, short enough that a banner from several
    // minutes ago cannot still be sitting there looking like a fresh event.
    private static readonly TimeSpan DefaultNoticeDuration = TimeSpan.FromSeconds(6);

    private readonly TimeProvider _clock;
    private readonly TimeSpan _noticeDuration;
    private readonly SeverityCrossingTracker _crossingTracker = new();
    private readonly ITimer _noticeTimer;

    public OverlayViewModel(TimeProvider? clock = null, TimeSpan? noticeDuration = null)
    {
        _clock = clock ?? TimeProvider.System;
        _noticeDuration = noticeDuration ?? DefaultNoticeDuration;
        OpenAi = new ProviderCellViewModel(ProviderId.OpenAi, "OpenAI / Codex");
        Anthropic = new ProviderCellViewModel(ProviderId.Anthropic, "Anthropic / Claude Code");
        // The callback can run on a thread pool thread, so it is posted back to the UI thread rather than
        // touching an observable property directly from there.
        _noticeTimer = _clock.CreateTimer(_ => Dispatcher.UIThread.Post(DismissNotice), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public ProviderCellViewModel OpenAi { get; }

    public ProviderCellViewModel Anthropic { get; }

    /// <summary>
    /// The user's currently configured warning/critical bands. Kept as a property rather than a
    /// constructor-only value, because the settings window can change it while the overlay is running.
    /// </summary>
    public SeverityThresholds Thresholds { get; set; } = SeverityThresholds.Default;

    [ObservableProperty]
    private OverlayPresentation _presentation = OverlayPresentation.Hidden;

    [ObservableProperty]
    private bool _reducedMotion;

    [ObservableProperty]
    private double _uiScale = 1.0;

    [ObservableProperty]
    private ProviderCellViewModel? _detailProvider;

    [ObservableProperty]
    private NoticeViewModel? _activeNotice;

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
        var display = QuotaDisplay.From(cell.DisplayName, state, now, Thresholds);
        cell.Apply(display, statusOverride ?? DescribeStatus(state), state.Activity, state, now, Thresholds);
        OnPropertyChanged(nameof(ShowsBusyMotion));

        // A provider back at Normal clears its own notice immediately rather than waiting out the timer,
        // so the banner never keeps pointing at a reading that already recovered.
        if (display.Severity == QuotaSeverity.Normal && ActiveNotice?.Provider == state.Connection.Provider)
        {
            DismissNotice();
        }

        if (_crossingTracker.Apply(state.Connection.Provider, display.Severity) is { } crossing)
        {
            ActiveNotice = new NoticeViewModel(crossing.Provider, crossing.Severity, DescribeNotice(cell.DisplayName, crossing.Severity));
            _noticeTimer.Change(_noticeDuration, Timeout.InfiniteTimeSpan);
        }
    }

    [RelayCommand]
    private void DismissNotice()
    {
        _noticeTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ActiveNotice = null;
    }

    private static string DescribeNotice(string displayName, QuotaSeverity severity) => severity == QuotaSeverity.Exhausted
        ? $"{displayName} reached the critical usage level"
        : $"{displayName} reached the warning usage level";

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
