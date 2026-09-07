using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using UseNotch.App.ViewModels;
using UseNotch.App.Views;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.UI.Tests;

public class OverlayScenarioTests
{
    private static ProviderRuntimeState State(
        ProviderId provider,
        decimal? usedFraction,
        AuthenticationState authentication = AuthenticationState.Authenticated,
        DataFreshness freshness = DataFreshness.Fresh,
        ActivityReading? activity = null,
        StateOrigin origin = StateOrigin.Live)
    {
        var now = DateTimeOffset.UtcNow;
        UsageSnapshot? snapshot = null;
        if (usedFraction is { } fraction)
        {
            var window = new QuotaWindow("session", "5h limit", TimeSpan.FromHours(5), now.AddHours(-1), now.AddHours(4), new UsageLimit(null, null, null, fraction, "percent"));
            snapshot = new UsageSnapshot(provider, new AccountScope("account", IdentityConfidence.LocalPartition), now, [window], "session", new SourceDescriptor("test", "m10", false), null);
        }

        return new ProviderRuntimeState(
            new ProviderConnection(provider, "test", true, 1),
            snapshot,
            new ProviderStatus(authentication, freshness, now, snapshot is null ? null : now, null, false, null),
            0,
            0)
        {
            Origin = origin,
            Activity = activity,
        };
    }

    private static ActivityReading Activity(ActivityState state, ReadingFidelity fidelity = ReadingFidelity.ProviderReported)
    {
        var now = DateTimeOffset.UtcNow;
        return ActivityReading.Supported(ProviderId.OpenAi, now, new ActivitySession("s", ProviderId.OpenAi, state, now, now, fidelity, null), 1);
    }

    [AvaloniaFact]
    public void Development_scenario_updates_both_provider_cells_without_network_access()
    {
        var viewModel = new OverlayViewModel();

        viewModel.ApplyScenario(ProviderId.OpenAi, MockScenario.Authenticated);
        viewModel.ApplyScenario(ProviderId.Anthropic, MockScenario.Missing);

        Assert.Equal("40% used", viewModel.OpenAi.Headline);
        Assert.Equal("Connected", viewModel.OpenAi.StatusText);
        Assert.Equal("-", viewModel.Anthropic.Headline);
        Assert.Equal("Sign in required", viewModel.Anthropic.StatusText);
    }

    [AvaloniaFact]
    public void Estimated_activity_is_labeled_in_the_cell_and_the_details()
    {
        var viewModel = new OverlayViewModel();

        viewModel.ApplyScenario(ProviderId.OpenAi, MockScenario.Estimated);
        viewModel.ShowDetail(ProviderId.OpenAi);

        Assert.Contains("estimated", viewModel.OpenAi.ActivityText, StringComparison.OrdinalIgnoreCase);
        Assert.Same(viewModel.OpenAi, viewModel.DetailProvider);
        Assert.Contains("estimated", viewModel.DetailProvider!.ActivityText, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void Provider_error_does_not_present_an_unavailable_quota_as_an_updated_window()
    {
        var viewModel = new OverlayViewModel();
        var state = new ProviderRuntimeState(
            new ProviderConnection(ProviderId.OpenAi, "test", true, 1),
            null,
            new ProviderStatus(AuthenticationState.Discovering, DataFreshness.Unknown, null, null, null, false, new ErrorState(ErrorCategory.Schema, false, "Provider response format is unsupported", null, null)),
            0,
            0);

        viewModel.ApplyRuntimeState(state);

        Assert.Equal("-", viewModel.OpenAi.Headline);
        Assert.Null(viewModel.OpenAi.RingFraction);
        Assert.Equal("Provider response format is unsupported", viewModel.OpenAi.StatusText);
    }

    [AvaloniaTheory]
    [InlineData(0.0, "0% used", QuotaSeverity.Normal)]
    [InlineData(0.85, "85% used", QuotaSeverity.Caution)]
    [InlineData(1.0, "100% used", QuotaSeverity.Exhausted)]
    [InlineData(1.2, "120% used", QuotaSeverity.Exhausted)]
    public void Supported_ring_values_keep_numeric_and_graphical_meaning_consistent(double used, string expectedText, QuotaSeverity expectedSeverity)
    {
        var viewModel = new OverlayViewModel();

        viewModel.ApplyRuntimeState(State(ProviderId.OpenAi, (decimal)used));

        Assert.Equal(expectedText, viewModel.OpenAi.Headline);
        Assert.Equal(expectedSeverity, viewModel.OpenAi.Severity);
        Assert.Equal(Math.Clamp(used, 0, 1), viewModel.OpenAi.RingFraction);
    }

    [AvaloniaFact]
    public void A_missing_reading_draws_no_arc_rather_than_a_zero_percent_ring()
    {
        var viewModel = new OverlayViewModel();

        viewModel.ApplyRuntimeState(State(ProviderId.Anthropic, null));

        Assert.Null(viewModel.Anthropic.RingFraction);
        Assert.Equal("-", viewModel.Anthropic.Headline);
        Assert.NotEqual(0d, viewModel.Anthropic.RingFraction);
    }

    [AvaloniaFact]
    public void Provider_colour_is_not_the_severity_colour_and_scope_text_is_always_present()
    {
        var viewModel = new OverlayViewModel();

        viewModel.ApplyRuntimeState(State(ProviderId.OpenAi, 0.2m));
        viewModel.ApplyRuntimeState(State(ProviderId.Anthropic, 0.95m));

        Assert.Equal("5h limit", viewModel.OpenAi.ScopeText);
        Assert.Equal("5h limit", viewModel.Anthropic.ScopeText);
        Assert.NotEqual(
            SeverityConverters.BrushFor(viewModel.OpenAi.Severity),
            SeverityConverters.BrushFor(viewModel.Anthropic.Severity));
        Assert.NotEqual(ProviderGlyphs.For(ProviderId.OpenAi), ProviderGlyphs.For(ProviderId.Anthropic));
    }

    [AvaloniaFact]
    public void An_automation_name_carries_provider_scope_value_and_freshness()
    {
        var viewModel = new OverlayViewModel();

        viewModel.ApplyRuntimeState(State(ProviderId.OpenAi, 0.4m));

        var name = viewModel.OpenAi.AutomationName;
        Assert.Contains("OpenAI / Codex", name, StringComparison.Ordinal);
        Assert.Contains("5h limit", name, StringComparison.Ordinal);
        Assert.Contains("40% used", name, StringComparison.Ordinal);
        Assert.Contains("just now", name, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void A_cached_reading_says_so_in_its_freshness_text()
    {
        var viewModel = new OverlayViewModel();

        viewModel.ApplyRuntimeState(State(ProviderId.OpenAi, 0.4m, origin: StateOrigin.CachedStartup));

        Assert.StartsWith("Cached", viewModel.OpenAi.FreshnessText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Every_compact_number_has_a_named_meaning_in_the_details()
    {
        var viewModel = new OverlayViewModel();
        viewModel.ApplyRuntimeState(State(ProviderId.OpenAi, 0.4m));

        viewModel.ShowDetail(ProviderId.OpenAi);

        var row = Assert.Single(viewModel.DetailProvider!.Windows);
        Assert.Equal("5h limit", row.Label);
        Assert.Equal("40% Used", row.UsedText);
        Assert.Equal(.4, row.Fraction, 3);
        Assert.StartsWith("Resets in", row.ResetText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void An_over_limit_window_is_explained_rather_than_silently_clamped()
    {
        var viewModel = new OverlayViewModel();
        viewModel.ApplyRuntimeState(State(ProviderId.OpenAi, 1.2m));

        viewModel.ShowDetail(ProviderId.OpenAi);

        // The ring stops at a full circle while the number keeps telling the truth.
        Assert.Equal("120% used", viewModel.OpenAi.Headline);
        Assert.Equal(1.0, viewModel.OpenAi.RingFraction);
        Assert.Equal(QuotaSeverity.Exhausted, Assert.Single(viewModel.DetailProvider!.Windows).Severity);
    }

    [AvaloniaFact]
    public void Busy_motion_runs_only_with_fresh_activity_and_stops_when_hidden_or_reduced()
    {
        var viewModel = new OverlayViewModel();
        viewModel.Apply(OverlayTrigger.Show);
        viewModel.ApplyRuntimeState(State(ProviderId.OpenAi, 0.4m, activity: Activity(ActivityState.Working)));

        Assert.True(viewModel.ShowsBusyMotion);

        viewModel.ReducedMotion = true;
        Assert.False(viewModel.ShowsBusyMotion);

        viewModel.ReducedMotion = false;
        viewModel.Apply(OverlayTrigger.Hide);
        Assert.False(viewModel.ShowsBusyMotion);
    }

    [AvaloniaFact]
    public void Unknown_activity_never_starts_busy_motion()
    {
        var viewModel = new OverlayViewModel();
        viewModel.Apply(OverlayTrigger.Show);

        viewModel.ApplyRuntimeState(State(ProviderId.OpenAi, 0.4m, activity: Activity(ActivityState.Unknown)));

        Assert.False(viewModel.ShowsBusyMotion);
        Assert.Equal("Activity unknown", viewModel.OpenAi.ActivityText);
    }

    [AvaloniaFact]
    public void Overlay_regions_follow_the_visible_geometry_without_hover_padding()
    {
        var viewModel = new OverlayViewModel();
        var window = new OverlayWindow(viewModel);
        try
        {
            window.Show();
            window.SetPresentation(OverlayTrigger.Show);
            var collapsed = window.GetInteractiveRegions();

            window.SetPresentation(OverlayTrigger.PointerEntered);
            var expanded = window.GetInteractiveRegions();

            // Collapsed draws nothing and captures nothing. Only an invisible edge strip is watched, and
            // watching is not capturing.
            Assert.Empty(collapsed.Regions);
            var strip = Assert.Single(collapsed.HoverRegions);
            Assert.True(strip.X > window.Width - 16, $"The trigger strip started at {strip.X}.");

            Assert.Equal(2, expanded.Regions.Count);
            Assert.All(expanded.Regions, region => Assert.True(region.Width > 0 && region.Height > 0));
        }
        finally
        {
            window.CloseForShutdown();
        }
    }

    [AvaloniaFact]
    public void Opening_details_adds_the_detail_panel_to_the_interactive_regions()
    {
        var viewModel = new OverlayViewModel();
        var window = new OverlayWindow(viewModel);
        try
        {
            window.Show();
            window.SetPresentation(OverlayTrigger.Show);
            window.SetPresentation(OverlayTrigger.PointerEntered);
            var beforeDetails = window.GetInteractiveRegions().Regions.Count;

            viewModel.ShowDetail(ProviderId.OpenAi);
            window.SetPresentation(OverlayTrigger.PinToggled);
            window.SetPresentation(OverlayTrigger.PinToggled);

            Assert.Equal(2, beforeDetails);
            Assert.Equal("UseNotch overlay expanded", window.Title);
            Assert.Equal(3, window.GetInteractiveRegions().Regions.Count);
        }
        finally
        {
            window.CloseForShutdown();
        }
    }

    [AvaloniaFact]
    public void The_detail_panel_stays_within_a_bounded_size()
    {
        var window = new OverlayWindow(new OverlayViewModel());
        try
        {
            window.Show();
            var panel = window.FindControl<Grid>("DetailPanel");
            var bubble = panel?.GetVisualDescendants().OfType<Border>().FirstOrDefault();

            Assert.NotNull(panel);
            Assert.NotNull(bubble);
            Assert.InRange(bubble.Width, 260, 340);
            Assert.True(panel.Bounds.Height <= window.Height);
        }
        finally
        {
            window.CloseForShutdown();
        }
    }

    [AvaloniaFact]
    public void Interactive_controls_carry_automation_names()
    {
        var viewModel = new OverlayViewModel();
        viewModel.ApplyRuntimeState(State(ProviderId.OpenAi, 0.4m));
        var window = new OverlayWindow(viewModel);
        try
        {
            window.Show();
            window.SetPresentation(OverlayTrigger.Show);
            window.SetPresentation(OverlayTrigger.PointerEntered);

            var cell = window.FindControl<Button>("OpenAiCell");
            var close = window.FindControl<Button>("CloseDetail");

            Assert.NotNull(cell);
            Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(cell)));
            Assert.NotNull(close);
            Assert.Equal("Close provider details", Avalonia.Automation.AutomationProperties.GetName(close));
        }
        finally
        {
            window.CloseForShutdown();
        }
    }

    [AvaloniaFact]
    public void Severity_brushes_are_distinct_so_colour_is_never_the_only_difference()
    {
        var brushes = new[]
        {
            SeverityConverters.BrushFor(QuotaSeverity.Normal),
            SeverityConverters.BrushFor(QuotaSeverity.Caution),
            SeverityConverters.BrushFor(QuotaSeverity.Exhausted),
            SeverityConverters.BrushFor(QuotaSeverity.Unavailable),
        }.Cast<ISolidColorBrush>().Select(brush => brush.Color).ToArray();

        Assert.Equal(brushes.Length, brushes.Distinct().Count());
    }
}
