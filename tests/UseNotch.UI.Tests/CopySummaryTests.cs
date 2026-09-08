using Avalonia.Headless.XUnit;
using UseNotch.App.ViewModels;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.UI.Tests;

public class CopySummaryTests
{
    private static ProviderRuntimeState State(ProviderId provider, params (string Id, string Scope, decimal? UsedFraction, DateTimeOffset? ResetsAt)[] windows)
    {
        var now = DateTimeOffset.UtcNow;
        var quotaWindows = windows
            .Select(window => new QuotaWindow(window.Id, window.Scope, TimeSpan.FromHours(5), now.AddHours(-1), window.ResetsAt, new UsageLimit(null, null, null, window.UsedFraction, "percent")))
            .ToArray();
        var snapshot = new UsageSnapshot(provider, new AccountScope("account", IdentityConfidence.LocalPartition), now, quotaWindows, quotaWindows[0].Id, new SourceDescriptor("test", "m10", false), null);

        return new ProviderRuntimeState(
            new ProviderConnection(provider, "test", true, 1),
            snapshot,
            new ProviderStatus(AuthenticationState.Authenticated, DataFreshness.Fresh, now, now, null, false, null),
            0,
            0);
    }

    [AvaloniaFact]
    public void A_normal_reading_summarizes_the_provider_name_and_window()
    {
        var viewModel = new OverlayViewModel();
        var resetsAt = DateTimeOffset.UtcNow.AddHours(3);
        viewModel.ApplyRuntimeState(State(ProviderId.Anthropic, ("session", "session", 0.42m, resetsAt)));

        var summary = viewModel.Anthropic.BuildCopySummary();

        Assert.StartsWith("Anthropic / Claude Code - session 42% Used (", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("account", summary, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void Multiple_windows_are_joined_into_one_line()
    {
        var viewModel = new OverlayViewModel();
        var resetsAt = DateTimeOffset.UtcNow.AddHours(3);
        viewModel.ApplyRuntimeState(State(
            ProviderId.OpenAi,
            ("session", "session", 0.42m, resetsAt),
            ("weekly", "weekly", 0.18m, resetsAt.AddDays(3))));

        var summary = viewModel.OpenAi.BuildCopySummary();

        Assert.Contains("session 42% Used", summary, StringComparison.Ordinal);
        Assert.Contains("weekly 18% Used", summary, StringComparison.Ordinal);
        Assert.Contains(", ", summary, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void An_over_limit_reading_keeps_its_true_percentage_in_the_summary()
    {
        var viewModel = new OverlayViewModel();
        viewModel.ApplyRuntimeState(State(ProviderId.OpenAi, ("session", "session", 1.35m, null)));

        var summary = viewModel.OpenAi.BuildCopySummary();

        Assert.Contains("135% Used", summary, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void An_unavailable_window_still_produces_a_readable_summary()
    {
        var viewModel = new OverlayViewModel();
        viewModel.ApplyRuntimeState(State(ProviderId.OpenAi, ("session", "session", null, null)));

        var summary = viewModel.OpenAi.BuildCopySummary();

        Assert.StartsWith("OpenAI / Codex - ", summary, StringComparison.Ordinal);
        Assert.Contains("Value unavailable", summary, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void A_provider_with_no_reading_at_all_summarizes_to_just_its_name()
    {
        var viewModel = new OverlayViewModel();

        var summary = viewModel.OpenAi.BuildCopySummary();

        Assert.Equal("OpenAI / Codex", summary);
    }
}
