using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.Application.Tests;

public class MockScenarioTests
{
    [Theory]
    [InlineData(MockScenario.Loading)]
    [InlineData(MockScenario.Missing)]
    [InlineData(MockScenario.Expired)]
    [InlineData(MockScenario.Rejected)]
    [InlineData(MockScenario.Forbidden)]
    [InlineData(MockScenario.Unsupported)]
    [InlineData(MockScenario.RateLimited)]
    [InlineData(MockScenario.Error)]
    public void Failure_scenarios_never_publish_authoritative_usage(MockScenario scenario)
    {
        var state = MockScenarioCatalog.Create(scenario);

        Assert.Null(state.Snapshot);
    }

    [Theory]
    [InlineData(MockScenario.Authenticated, AuthenticationState.Authenticated, DataFreshness.Fresh)]
    [InlineData(MockScenario.Stale, AuthenticationState.Authenticated, DataFreshness.Stale)]
    [InlineData(MockScenario.Working, AuthenticationState.Authenticated, DataFreshness.Fresh)]
    [InlineData(MockScenario.Estimated, AuthenticationState.Authenticated, DataFreshness.Fresh)]
    public void Reading_scenarios_preserve_status_and_headline(MockScenario scenario, AuthenticationState authentication, DataFreshness freshness)
    {
        var state = MockScenarioCatalog.Create(scenario);

        Assert.NotNull(state.Snapshot);
        Assert.Equal(authentication, state.Status.Authentication);
        Assert.Equal(freshness, state.Status.Freshness);
        Assert.Equal("session", state.Snapshot.HeadlineWindowId);
    }
}
