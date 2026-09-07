using UseNotch.App.ViewModels;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.UI.Tests;

public class OverlayScenarioTests
{
    [Fact]
    public void Development_scenario_updates_both_provider_cells_without_network_access()
    {
        var viewModel = new OverlayViewModel();

        viewModel.ApplyScenario(ProviderId.OpenAi, MockScenario.Authenticated);
        viewModel.ApplyScenario(ProviderId.Anthropic, MockScenario.Missing);

        Assert.Equal("40% used", viewModel.OpenAiHeadline);
        Assert.Equal("Connected", viewModel.OpenAiStatusText);
        Assert.Equal("Awaiting updated window", viewModel.AnthropicHeadline);
        Assert.Equal("Sign in required", viewModel.AnthropicStatusText);
    }

    [Fact]
    public void Estimated_activity_is_labeled_in_details()
    {
        var viewModel = new OverlayViewModel();

        viewModel.ApplyScenario(ProviderId.OpenAi, MockScenario.Estimated);

        Assert.Contains("estimated", viewModel.ActivityText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("estimated", viewModel.DetailText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
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

        Assert.Equal("Usage unavailable", viewModel.OpenAiHeadline);
        Assert.Equal("Provider response format is unsupported", viewModel.OpenAiStatusText);
    }
}
