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

    [Fact]
    public void Reset_passed_fixture_does_not_claim_a_current_headline()
    {
        var state = MockScenarioCatalog.Create(MockScenario.ResetPassed);

        Assert.NotNull(state.Snapshot);
        Assert.Null(state.Snapshot.Headline);
        Assert.True(state.Snapshot.Windows[0].ResetsAt < state.Snapshot.RetrievedAt);
    }

    [Fact]
    public void Registry_exposes_only_the_two_supported_provider_families()
    {
        var definitions = new ProviderRegistry().Definitions;

        Assert.Equal([ProviderId.OpenAi, ProviderId.Anthropic], definitions.Select(definition => definition.Id));
    }

    [Theory]
    [InlineData(MockScenario.ZeroUsage, 0)]
    [InlineData(MockScenario.FullUsage, 1)]
    [InlineData(MockScenario.OverLimit, 1)]
    public void Ring_fraction_fixtures_preserve_numeric_semantics(MockScenario scenario, decimal expectedDisplayFraction)
    {
        var snapshot = MockScenarioCatalog.Create(scenario).Snapshot;

        Assert.NotNull(snapshot);
        Assert.Equal(expectedDisplayFraction, snapshot.Headline!.Limit.DisplayFraction);
    }
}
