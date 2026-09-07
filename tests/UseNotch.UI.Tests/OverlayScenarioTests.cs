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
}
