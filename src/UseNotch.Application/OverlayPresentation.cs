using UseNotch.Domain;

namespace UseNotch.Application;

public enum OverlayPresentationState { Hidden, Collapsed, Expanded, ProviderDetailsOpen, Pinned }

public enum OverlayTrigger { Show, Hide, PointerEntered, PointerExited, ProviderActivated, DetailsClosed, PinToggled }

/// <summary>
/// The overlay's presentation states and the only transitions between them. Keeping this pure means the
/// hover, expansion, and pin behaviour can be tested without a window, a monitor, or a pointer.
/// </summary>
public sealed record OverlayPresentation(OverlayPresentationState State, bool Pinned)
{
    public static OverlayPresentation Hidden { get; } = new(OverlayPresentationState.Hidden, false);

    public bool IsVisible => State != OverlayPresentationState.Hidden;

    public bool ShowsDetails => State == OverlayPresentationState.ProviderDetailsOpen;

    /// <summary>
    /// True while the overlay shows more than its collapsed handle. Callers use this to decide whether
    /// the visible geometry, and therefore the native input regions, changed.
    /// </summary>
    public bool ShowsProviderCells => State is OverlayPresentationState.Expanded or OverlayPresentationState.ProviderDetailsOpen or OverlayPresentationState.Pinned;

    public OverlayPresentation Apply(OverlayTrigger trigger) => trigger switch
    {
        OverlayTrigger.Hide => this with { State = OverlayPresentationState.Hidden },
        OverlayTrigger.Show => State == OverlayPresentationState.Hidden
            ? this with { State = Pinned ? OverlayPresentationState.Pinned : OverlayPresentationState.Collapsed }
            : this,
        OverlayTrigger.PinToggled => ApplyPin(),
        _ when State == OverlayPresentationState.Hidden => this,
        OverlayTrigger.PointerEntered => State == OverlayPresentationState.Collapsed
            ? this with { State = OverlayPresentationState.Expanded }
            : this,
        // Leaving the overlay never closes an open detail panel or a pinned surface. Only a collapse-
        // eligible expansion follows the pointer.
        OverlayTrigger.PointerExited => State == OverlayPresentationState.Expanded
            ? this with { State = OverlayPresentationState.Collapsed }
            : this,
        OverlayTrigger.ProviderActivated => State == OverlayPresentationState.Collapsed || State == OverlayPresentationState.Expanded || State == OverlayPresentationState.Pinned
            ? this with { State = OverlayPresentationState.ProviderDetailsOpen }
            : this,
        OverlayTrigger.DetailsClosed => State == OverlayPresentationState.ProviderDetailsOpen
            ? this with { State = Pinned ? OverlayPresentationState.Pinned : OverlayPresentationState.Collapsed }
            : this,
        _ => this,
    };

    private OverlayPresentation ApplyPin()
    {
        var pinned = !Pinned;
        if (State == OverlayPresentationState.Hidden)
        {
            return this with { Pinned = pinned };
        }

        if (pinned)
        {
            return State == OverlayPresentationState.ProviderDetailsOpen
                ? this with { Pinned = true }
                : new OverlayPresentation(OverlayPresentationState.Pinned, true);
        }

        return State == OverlayPresentationState.Pinned
            ? new OverlayPresentation(OverlayPresentationState.Collapsed, false)
            : this with { Pinned = false };
    }
}

public sealed record OverlayHoverDelays(TimeSpan Expand, TimeSpan Collapse)
{
    // Bounded on both sides: long enough that a pointer crossing the edge does not flash the overlay
    // open, short enough that a deliberate hover feels immediate.
    public static OverlayHoverDelays Default { get; } = new(TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(260));
}

public enum QuotaSeverity { None, Normal, Caution, Exhausted, Unavailable }

/// <summary>
/// The presentation facts for one quota reading. Severity is deliberately separate from provider
/// identity, so a provider's colour never doubles as a warning and a warning never hides the provider.
/// </summary>
public sealed record QuotaDisplay(
    string ValueText,
    string ScopeText,
    string FreshnessText,
    QuotaSeverity Severity,
    double? RingFraction,
    bool IsOverLimit,
    string AutomationName)
{
    public const double CautionThreshold = 0.8;

    public static QuotaDisplay From(string providerName, ProviderRuntimeState? state, DateTimeOffset now)
    {
        if (state is null)
        {
            return Unavailable(providerName, "Reading usage", "Loading");
        }

        if (state.Status.Authentication is AuthenticationState.Missing or AuthenticationState.Expired
            or AuthenticationState.Rejected or AuthenticationState.AccessDenied or AuthenticationState.Unsupported)
        {
            return Unavailable(providerName, DescribeAuthentication(state.Status.Authentication), "Quota unavailable");
        }

        if (state.Snapshot?.Headline is not { } headline)
        {
            var reason = state.Status.Error is not null ? "Usage unavailable" : "Awaiting updated window";
            return Unavailable(providerName, reason, reason);
        }

        var fraction = headline.Limit.UsedFraction;
        if (fraction is not { } used)
        {
            return Unavailable(providerName, "Value unavailable for this window", "Value unavailable");
        }

        // Over-limit readings stay honest in the text while the ring stops at a full circle.
        var overLimit = used > 1m;
        var percent = Math.Round(used * 100m, MidpointRounding.AwayFromZero);
        var valueText = $"{percent:0}% used";
        var freshness = DescribeFreshness(state, now);
        var severity = state.Status.Freshness == DataFreshness.Expired
            ? QuotaSeverity.Unavailable
            : overLimit || used >= 1m
                ? QuotaSeverity.Exhausted
                : used >= (decimal)CautionThreshold
                    ? QuotaSeverity.Caution
                    : QuotaSeverity.Normal;
        return new QuotaDisplay(
            valueText,
            headline.Scope,
            freshness,
            severity,
            Math.Clamp((double)used, 0, 1),
            overLimit,
            $"{providerName}, {headline.Scope}, {valueText}, {freshness}");
    }

    private static QuotaDisplay Unavailable(string providerName, string scope, string valueText)
        => new("-", scope, valueText, QuotaSeverity.Unavailable, null, false, $"{providerName}, {scope}, no reading");

    private static string DescribeAuthentication(AuthenticationState state) => state switch
    {
        AuthenticationState.Missing => "Sign-in required",
        AuthenticationState.Expired => "Session expired",
        AuthenticationState.Rejected => "Credential rejected",
        AuthenticationState.AccessDenied => "Access denied",
        _ => "Unsupported source",
    };

    private static string DescribeFreshness(ProviderRuntimeState state, DateTimeOffset now)
    {
        if (state.Status.LastSuccess is not { } success)
        {
            return "No successful reading yet";
        }

        var age = now - success;
        var ageText = age < TimeSpan.FromMinutes(1)
            ? "just now"
            : age < TimeSpan.FromHours(1)
                ? $"{age.TotalMinutes:0} min ago"
                : $"{age.TotalHours:0} h ago";
        return state.Origin == StateOrigin.CachedStartup ? $"Cached, {ageText}" : ageText;
    }
}

/// <summary>
/// Ring geometry for a small purpose-built control. Values are clamped so a full circle and an
/// over-limit reading are visually identical, while the numeric text keeps them apart.
/// </summary>
public static class QuotaRingGeometry
{
    public const double StartAngleDegrees = -90;

    public static double SweepDegrees(double? fraction)
        => fraction is { } value ? Math.Clamp(value, 0, 1) * 360 : 0;

    public static bool IsFullCircle(double? fraction) => fraction is { } value && value >= 1;

    public static bool HasVisibleArc(double? fraction) => fraction is { } value && value > 0;

    /// <summary>
    /// The point on the ring at a given fraction. The caller draws from the start point to this one, so
    /// a zero reading has no arc at all rather than a misleading minimum stub.
    /// </summary>
    public static (double X, double Y) PointAt(double centreX, double centreY, double radius, double fraction)
    {
        var angle = (StartAngleDegrees + (Math.Clamp(fraction, 0, 1) * 360)) * Math.PI / 180;
        return (centreX + (radius * Math.Cos(angle)), centreY + (radius * Math.Sin(angle)));
    }
}
