using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.Application.Tests;

public class OverlayPresentationTests
{
    private static OverlayPresentation Visible()
        => OverlayPresentation.Hidden.Apply(OverlayTrigger.Show);

    [Fact]
    public void Showing_starts_collapsed_and_hiding_returns_to_hidden()
    {
        var shown = Visible();

        Assert.Equal(OverlayPresentationState.Collapsed, shown.State);
        Assert.Equal(OverlayPresentationState.Hidden, shown.Apply(OverlayTrigger.Hide).State);
    }

    [Fact]
    public void Hover_expands_and_collapses_without_touching_the_hidden_state()
    {
        var expanded = Visible().Apply(OverlayTrigger.PointerEntered);

        Assert.Equal(OverlayPresentationState.Expanded, expanded.State);
        Assert.Equal(OverlayPresentationState.Collapsed, expanded.Apply(OverlayTrigger.PointerExited).State);
        Assert.Equal(OverlayPresentationState.Hidden, OverlayPresentation.Hidden.Apply(OverlayTrigger.PointerEntered).State);
    }

    [Fact]
    public void Leaving_the_overlay_never_closes_open_details()
    {
        var details = Visible().Apply(OverlayTrigger.PointerEntered).Apply(OverlayTrigger.ProviderActivated);

        Assert.Equal(OverlayPresentationState.ProviderDetailsOpen, details.Apply(OverlayTrigger.PointerExited).State);
    }

    [Fact]
    public void Closing_details_returns_to_collapsed_or_to_pinned()
    {
        var details = Visible().Apply(OverlayTrigger.ProviderActivated);
        Assert.Equal(OverlayPresentationState.Collapsed, details.Apply(OverlayTrigger.DetailsClosed).State);

        var pinnedDetails = details.Apply(OverlayTrigger.PinToggled);
        Assert.True(pinnedDetails.Pinned);
        Assert.Equal(OverlayPresentationState.ProviderDetailsOpen, pinnedDetails.State);
        Assert.Equal(OverlayPresentationState.Pinned, pinnedDetails.Apply(OverlayTrigger.DetailsClosed).State);
    }

    [Fact]
    public void A_pinned_overlay_does_not_collapse_when_the_pointer_leaves()
    {
        var pinned = Visible().Apply(OverlayTrigger.PinToggled);

        Assert.Equal(OverlayPresentationState.Pinned, pinned.State);
        Assert.Equal(OverlayPresentationState.Pinned, pinned.Apply(OverlayTrigger.PointerExited).State);
        Assert.True(pinned.ShowsProviderCells);
    }

    [Fact]
    public void Unpinning_returns_to_the_collapsed_state()
    {
        var pinned = Visible().Apply(OverlayTrigger.PinToggled);

        var unpinned = pinned.Apply(OverlayTrigger.PinToggled);

        Assert.False(unpinned.Pinned);
        Assert.Equal(OverlayPresentationState.Collapsed, unpinned.State);
    }

    [Fact]
    public void A_pin_set_while_hidden_takes_effect_on_the_next_show()
    {
        var pinnedWhileHidden = OverlayPresentation.Hidden.Apply(OverlayTrigger.PinToggled);

        Assert.Equal(OverlayPresentationState.Hidden, pinnedWhileHidden.State);
        Assert.Equal(OverlayPresentationState.Pinned, pinnedWhileHidden.Apply(OverlayTrigger.Show).State);
    }

    [Fact]
    public void Only_the_collapsed_state_hides_the_provider_cells()
    {
        Assert.False(Visible().ShowsProviderCells);
        Assert.True(Visible().Apply(OverlayTrigger.PointerEntered).ShowsProviderCells);
        Assert.True(Visible().Apply(OverlayTrigger.ProviderActivated).ShowsProviderCells);
        Assert.False(OverlayPresentation.Hidden.ShowsProviderCells);
    }

    [Fact]
    public void Hover_delays_are_bounded_on_both_sides()
    {
        Assert.InRange(OverlayHoverDelays.Default.Expand, TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(300));
        Assert.InRange(OverlayHoverDelays.Default.Collapse, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(600));
        Assert.True(OverlayHoverDelays.Default.Collapse > OverlayHoverDelays.Default.Expand);
    }
}

public class QuotaRingGeometryTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 90.0)]
    [InlineData(1.0, 360.0)]
    [InlineData(1.5, 360.0)]
    public void The_sweep_is_clamped_to_one_full_circle(double fraction, double expected)
        => Assert.Equal(expected, QuotaRingGeometry.SweepDegrees(fraction), 6);

    [Fact]
    public void A_missing_reading_has_no_sweep_and_no_visible_arc()
    {
        Assert.Equal(0, QuotaRingGeometry.SweepDegrees(null));
        Assert.False(QuotaRingGeometry.HasVisibleArc(null));
        Assert.False(QuotaRingGeometry.IsFullCircle(null));
    }

    [Fact]
    public void A_zero_reading_draws_no_arc_so_it_cannot_be_mistaken_for_a_minimum()
        => Assert.False(QuotaRingGeometry.HasVisibleArc(0));

    [Fact]
    public void A_full_and_an_over_limit_reading_are_geometrically_identical()
    {
        Assert.True(QuotaRingGeometry.IsFullCircle(1.0));
        Assert.True(QuotaRingGeometry.IsFullCircle(1.5));
        Assert.Equal(QuotaRingGeometry.SweepDegrees(1.0), QuotaRingGeometry.SweepDegrees(1.5));
    }

    [Fact]
    public void The_arc_starts_at_the_top_of_the_ring()
    {
        var (x, y) = QuotaRingGeometry.PointAt(50, 50, 20, 0);

        Assert.Equal(50, x, 6);
        Assert.Equal(30, y, 6);
    }

    [Fact]
    public void A_quarter_turn_lands_on_the_right_of_the_ring()
    {
        var (x, y) = QuotaRingGeometry.PointAt(50, 50, 20, 0.25);

        Assert.Equal(70, x, 6);
        Assert.Equal(50, y, 6);
    }
}

public class QuotaDisplayTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static ProviderRuntimeState State(
        decimal? used,
        AuthenticationState authentication = AuthenticationState.Authenticated,
        DataFreshness freshness = DataFreshness.Fresh,
        DateTimeOffset? lastSuccess = null)
    {
        UsageSnapshot? snapshot = null;
        if (used is { } fraction)
        {
            var window = new QuotaWindow("session", "5h limit", TimeSpan.FromHours(5), Now.AddHours(-1), Now.AddHours(4), new UsageLimit(null, null, null, fraction, "percent"));
            snapshot = new UsageSnapshot(ProviderId.OpenAi, new AccountScope("account", IdentityConfidence.LocalPartition), Now, [window], "session", new SourceDescriptor("test", "m10", false), null);
        }

        return new ProviderRuntimeState(
            new ProviderConnection(ProviderId.OpenAi, "test", true, 1),
            snapshot,
            new ProviderStatus(authentication, freshness, Now, lastSuccess ?? (snapshot is null ? null : Now), null, false, null),
            0,
            0);
    }

    [Fact]
    public void A_loading_provider_shows_a_placeholder_rather_than_a_zero_reading()
    {
        var display = QuotaDisplay.From("OpenAI / Codex", null, Now);

        Assert.Equal("-", display.ValueText);
        Assert.Null(display.RingFraction);
        Assert.Equal(QuotaSeverity.Unavailable, display.Severity);
    }

    [Theory]
    [InlineData(AuthenticationState.Missing, "Sign-in required")]
    [InlineData(AuthenticationState.Expired, "Session expired")]
    [InlineData(AuthenticationState.Rejected, "Credential rejected")]
    [InlineData(AuthenticationState.AccessDenied, "Access denied")]
    [InlineData(AuthenticationState.Unsupported, "Unsupported source")]
    public void Each_authentication_problem_reads_as_itself_and_never_as_a_quota_value(AuthenticationState authentication, string expectedScope)
    {
        var display = QuotaDisplay.From("OpenAI / Codex", State(0.4m, authentication), Now);

        Assert.Equal("-", display.ValueText);
        Assert.Equal(expectedScope, display.ScopeText);
        Assert.Null(display.RingFraction);
    }

    [Fact]
    public void An_expired_reading_is_marked_unavailable_even_when_a_value_exists()
    {
        var display = QuotaDisplay.From("OpenAI / Codex", State(0.4m, freshness: DataFreshness.Expired), Now);

        Assert.Equal(QuotaSeverity.Unavailable, display.Severity);
        Assert.Equal("40% used", display.ValueText);
    }

    // Green below half, yellow to 79%, red from 80%. The boundaries are pinned on both sides so a
    // threshold cannot be nudged without a test saying so.
    [Theory]
    [InlineData(0.0, QuotaSeverity.Normal)]
    [InlineData(0.49, QuotaSeverity.Normal)]
    [InlineData(0.5, QuotaSeverity.Caution)]
    [InlineData(0.79, QuotaSeverity.Caution)]
    [InlineData(0.8, QuotaSeverity.Exhausted)]
    [InlineData(1.0, QuotaSeverity.Exhausted)]
    [InlineData(1.4, QuotaSeverity.Exhausted)]
    public void Severity_follows_the_reading_rather_than_the_provider(double used, QuotaSeverity expected)
        => Assert.Equal(expected, QuotaDisplay.From("OpenAI / Codex", State((decimal)used), Now).Severity);

    [Fact]
    public void An_over_limit_reading_keeps_its_true_number_while_the_ring_stops_at_full()
    {
        var display = QuotaDisplay.From("OpenAI / Codex", State(1.35m), Now);

        Assert.Equal("135% used", display.ValueText);
        Assert.True(display.IsOverLimit);
        Assert.Equal(1.0, display.RingFraction);
    }

    [Fact]
    public void Freshness_text_reports_the_age_of_the_last_success()
    {
        var display = QuotaDisplay.From("OpenAI / Codex", State(0.4m, lastSuccess: Now.AddMinutes(-20)), Now);

        Assert.Equal("20 min ago", display.FreshnessText);
    }

    [Fact]
    public void A_provider_with_no_successful_reading_says_so()
    {
        var state = State(null);

        var display = QuotaDisplay.From("OpenAI / Codex", state, Now);

        Assert.Equal("-", display.ValueText);
        Assert.Contains("no reading", display.AutomationName, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0.29, QuotaSeverity.Normal)]
    [InlineData(0.3, QuotaSeverity.Caution)]
    [InlineData(0.59, QuotaSeverity.Caution)]
    [InlineData(0.6, QuotaSeverity.Exhausted)]
    public void Custom_thresholds_change_severity_at_the_configured_boundary(double used, QuotaSeverity expected)
    {
        var thresholds = new SeverityThresholds(0.3, 0.6);

        var display = QuotaDisplay.From("OpenAI / Codex", State((decimal)used), Now, thresholds);

        Assert.Equal(expected, display.Severity);
    }

    [Theory]
    [InlineData(0.0, QuotaSeverity.Normal)]
    [InlineData(0.49, QuotaSeverity.Normal)]
    [InlineData(0.5, QuotaSeverity.Caution)]
    [InlineData(0.79, QuotaSeverity.Caution)]
    [InlineData(0.8, QuotaSeverity.Exhausted)]
    public void Default_thresholds_behave_exactly_as_before_when_passed_explicitly(double used, QuotaSeverity expected)
    {
        var display = QuotaDisplay.From("OpenAI / Codex", State((decimal)used), Now, SeverityThresholds.Default);

        Assert.Equal(expected, display.Severity);
    }
}

public class SeverityCrossingTrackerTests
{
    [Fact]
    public void Crossing_into_caution_then_critical_fires_exactly_two_notices()
    {
        var tracker = new SeverityCrossingTracker();

        var normal = tracker.Apply(ProviderId.OpenAi, QuotaSeverity.Normal);
        var caution = tracker.Apply(ProviderId.OpenAi, QuotaSeverity.Caution);
        var repeatedCaution = tracker.Apply(ProviderId.OpenAi, QuotaSeverity.Caution);
        var exhausted = tracker.Apply(ProviderId.OpenAi, QuotaSeverity.Exhausted);

        Assert.Null(normal);
        Assert.Equal(new SeverityCrossing(ProviderId.OpenAi, QuotaSeverity.Caution), caution);
        Assert.Null(repeatedCaution);
        Assert.Equal(new SeverityCrossing(ProviderId.OpenAi, QuotaSeverity.Exhausted), exhausted);
    }

    [Fact]
    public void Dropping_back_below_warning_and_crossing_again_fires_a_third_notice()
    {
        var tracker = new SeverityCrossingTracker();
        tracker.Apply(ProviderId.OpenAi, QuotaSeverity.Caution);
        tracker.Apply(ProviderId.OpenAi, QuotaSeverity.Exhausted);

        var afterDrop = tracker.Apply(ProviderId.OpenAi, QuotaSeverity.Normal);
        var thirdCrossing = tracker.Apply(ProviderId.OpenAi, QuotaSeverity.Caution);

        Assert.Null(afterDrop);
        Assert.Equal(new SeverityCrossing(ProviderId.OpenAi, QuotaSeverity.Caution), thirdCrossing);
    }

    [Fact]
    public void An_unavailable_or_none_reading_neither_fires_nor_resets_the_arm_state()
    {
        var tracker = new SeverityCrossingTracker();
        tracker.Apply(ProviderId.OpenAi, QuotaSeverity.Caution);

        var unavailable = tracker.Apply(ProviderId.OpenAi, QuotaSeverity.Unavailable);
        var none = tracker.Apply(ProviderId.OpenAi, QuotaSeverity.None);
        var repeated = tracker.Apply(ProviderId.OpenAi, QuotaSeverity.Caution);

        Assert.Null(unavailable);
        Assert.Null(none);
        Assert.Null(repeated);
    }

    [Fact]
    public void Two_providers_are_tracked_independently()
    {
        var tracker = new SeverityCrossingTracker();

        var openAi = tracker.Apply(ProviderId.OpenAi, QuotaSeverity.Caution);
        var anthropic = tracker.Apply(ProviderId.Anthropic, QuotaSeverity.Caution);

        Assert.Equal(new SeverityCrossing(ProviderId.OpenAi, QuotaSeverity.Caution), openAi);
        Assert.Equal(new SeverityCrossing(ProviderId.Anthropic, QuotaSeverity.Caution), anthropic);
    }
}
