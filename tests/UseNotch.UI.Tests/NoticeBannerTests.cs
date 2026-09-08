using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using UseNotch.App.ViewModels;
using UseNotch.App.Views;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.UI.Tests;

public class NoticeBannerTests
{
    private static ProviderRuntimeState State(decimal usedFraction)
    {
        var now = DateTimeOffset.UtcNow;
        var window = new QuotaWindow("session", "5h limit", TimeSpan.FromHours(5), now.AddHours(-1), now.AddHours(4), new UsageLimit(null, null, null, usedFraction, "percent"));
        var snapshot = new UsageSnapshot(ProviderId.OpenAi, new AccountScope("account", IdentityConfidence.LocalPartition), now, [window], "session", new SourceDescriptor("test", "m10", false), null);

        return new ProviderRuntimeState(
            new ProviderConnection(ProviderId.OpenAi, "test", true, 1),
            snapshot,
            new ProviderStatus(AuthenticationState.Authenticated, DataFreshness.Fresh, now, now, null, false, null),
            0,
            0);
    }

    [AvaloniaFact]
    public void A_session_that_crosses_warning_then_critical_produces_exactly_two_notices()
    {
        var viewModel = new OverlayViewModel();
        var seen = new List<QuotaSeverity>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.ActiveNotice) && viewModel.ActiveNotice is { } notice)
            {
                seen.Add(notice.Severity);
            }
        };

        viewModel.ApplyRuntimeState(State(0.2m)); // Normal, no notice.
        viewModel.ApplyRuntimeState(State(0.6m)); // Crosses into caution.
        viewModel.ApplyRuntimeState(State(0.65m)); // Still caution, no repeat notice.
        viewModel.ApplyRuntimeState(State(0.9m)); // Crosses into critical.

        Assert.Equal([QuotaSeverity.Caution, QuotaSeverity.Exhausted], seen);
    }

    [AvaloniaFact]
    public void Dropping_back_below_warning_and_crossing_again_produces_a_third_notice()
    {
        var viewModel = new OverlayViewModel();
        var seen = new List<QuotaSeverity>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.ActiveNotice) && viewModel.ActiveNotice is { } notice)
            {
                seen.Add(notice.Severity);
            }
        };

        viewModel.ApplyRuntimeState(State(0.6m)); // Crosses into caution: notice 1.
        viewModel.ApplyRuntimeState(State(0.2m)); // Drops back to normal: re-arms.
        viewModel.ApplyRuntimeState(State(0.55m)); // Crosses into caution again: notice 2.

        Assert.Equal([QuotaSeverity.Caution, QuotaSeverity.Caution], seen);
    }

    [AvaloniaFact]
    public void Dismissing_the_notice_clears_it_and_stops_the_timer()
    {
        var viewModel = new OverlayViewModel(noticeDuration: TimeSpan.FromMinutes(5));
        viewModel.ApplyRuntimeState(State(0.6m));
        Assert.NotNull(viewModel.ActiveNotice);

        viewModel.DismissNoticeCommand.Execute(null);

        Assert.Null(viewModel.ActiveNotice);
    }

    [AvaloniaFact]
    public void The_notice_auto_dismisses_after_its_configured_duration()
    {
        var viewModel = new OverlayViewModel(noticeDuration: TimeSpan.FromMilliseconds(30));
        viewModel.ApplyRuntimeState(State(0.6m));
        Assert.NotNull(viewModel.ActiveNotice);

        Thread.Sleep(200);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewModel.ActiveNotice);
    }

    [AvaloniaFact]
    public void Reduced_motion_does_not_prevent_the_notice_from_appearing_or_auto_dismissing()
    {
        var viewModel = new OverlayViewModel(noticeDuration: TimeSpan.FromMilliseconds(30)) { ReducedMotion = true };
        viewModel.ApplyRuntimeState(State(0.6m));

        Assert.NotNull(viewModel.ActiveNotice);

        Thread.Sleep(200);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewModel.ActiveNotice);
    }

    [AvaloniaFact]
    public void Notice_state_never_changes_the_presentation_visibility_contract()
    {
        var viewModel = new OverlayViewModel();
        viewModel.Apply(OverlayTrigger.Show);
        var beforeVisible = viewModel.Presentation.IsVisible;
        var beforeShowsCells = viewModel.ShowsProviderCells;

        viewModel.ApplyRuntimeState(State(0.9m));

        Assert.Equal(beforeVisible, viewModel.Presentation.IsVisible);
        Assert.Equal(beforeShowsCells, viewModel.ShowsProviderCells);
    }

    [AvaloniaFact]
    public void The_notice_banner_is_reported_as_an_interactive_region_only_while_visible()
    {
        var viewModel = new OverlayViewModel(noticeDuration: TimeSpan.FromMinutes(5));
        var window = new OverlayWindow(viewModel);
        try
        {
            window.Show();
            window.SetPresentation(OverlayTrigger.Show);
            var beforeNotice = window.GetInteractiveRegions().Regions.Count;

            viewModel.ApplyRuntimeState(State(0.9m));
            Dispatcher.UIThread.RunJobs();
            window.GetInteractiveRegions();

            var afterNotice = window.GetInteractiveRegions();
            Assert.True(afterNotice.Regions.Count > beforeNotice);

            viewModel.DismissNoticeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var afterDismiss = window.GetInteractiveRegions();
            Assert.Equal(beforeNotice, afterDismiss.Regions.Count);
        }
        finally
        {
            window.CloseForShutdown();
        }
    }
}
