using UseNotch.Domain;

namespace UseNotch.Application;

public interface IUsageProvider
{
    ProviderId Provider { get; }
    Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken);
}

public interface IProviderRegistry
{
    IReadOnlyList<ProviderDefinition> Definitions { get; }
}

public enum MockScenario { Loading, Authenticated, Missing, Expired, Rejected, Forbidden, Unsupported, Stale, ResetPassed, RateLimited, Error, Working, Waiting, Estimated, UnknownActivity, ZeroUsage, FullUsage, OverLimit }

public sealed record MockProviderState(UsageSnapshot? Snapshot, ProviderStatus Status, ActivitySession? Activity);

public sealed class ProviderRegistry : IProviderRegistry
{
    public IReadOnlyList<ProviderDefinition> Definitions { get; } =
    [
        new(ProviderId.OpenAi, "OpenAI / Codex", new HashSet<ProviderCapability> { ProviderCapability.Quota, ProviderCapability.Activity }),
        new(ProviderId.Anthropic, "Anthropic / Claude Code", new HashSet<ProviderCapability> { ProviderCapability.Quota, ProviderCapability.Activity }),
    ];
}

public static class MockScenarioCatalog
{
    public static MockProviderState Create(MockScenario scenario, TimeProvider? timeProvider = null) => Create(ProviderId.OpenAi, scenario, timeProvider);

    public static MockProviderState Create(ProviderId provider, MockScenario scenario, TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var now = clock.GetUtcNow();
        var authentication = scenario switch
        {
            MockScenario.Authenticated or MockScenario.Stale or MockScenario.ResetPassed or MockScenario.Working or MockScenario.Waiting or MockScenario.Estimated or MockScenario.ZeroUsage or MockScenario.FullUsage or MockScenario.OverLimit => AuthenticationState.Authenticated,
            MockScenario.Missing => AuthenticationState.Missing,
            MockScenario.Expired => AuthenticationState.Expired,
            MockScenario.Rejected => AuthenticationState.Rejected,
            MockScenario.Forbidden => AuthenticationState.AccessDenied,
            MockScenario.Unsupported => AuthenticationState.Unsupported,
            _ => AuthenticationState.Discovering,
        };
        var freshness = scenario is MockScenario.Stale or MockScenario.ResetPassed ? DataFreshness.Stale : DataFreshness.Fresh;
        var error = scenario switch
        {
            MockScenario.RateLimited => new ErrorState(ErrorCategory.RateLimited, true, "Rate limited", 429, now.AddMinutes(5)),
            MockScenario.Error => new ErrorState(ErrorCategory.Network, true, "Provider request failed", null, now.AddMinutes(1)),
            MockScenario.Forbidden => new ErrorState(ErrorCategory.Forbidden, false, "Access denied", 403, null),
            MockScenario.Unsupported => new ErrorState(ErrorCategory.Unsupported, false, "Credential storage is not supported", null, null),
            _ => null,
        };
        var activityState = scenario switch
        {
            MockScenario.Working => ActivityState.Working,
            MockScenario.Waiting => ActivityState.Waiting,
            // The estimated fixture exists to exercise the estimated label, so it needs an actual state
            // to label. An unknown state is covered by the UnknownActivity fixture instead.
            MockScenario.Estimated => ActivityState.Working,
            _ => ActivityState.Unknown,
        };
        var activity = scenario is MockScenario.Working or MockScenario.Waiting or MockScenario.Estimated
            ? new ActivitySession("mock-session", provider, activityState, now.AddMinutes(-2), now, scenario == MockScenario.Estimated ? ReadingFidelity.Derived : ReadingFidelity.ProviderReported, null)
            : null;
        var snapshot = scenario is MockScenario.Authenticated or MockScenario.Stale or MockScenario.ResetPassed or MockScenario.Working or MockScenario.Waiting or MockScenario.Estimated or MockScenario.ZeroUsage or MockScenario.FullUsage or MockScenario.OverLimit
            ? CreateSnapshot(provider, now, freshness, scenario)
            : null;
        return new MockProviderState(snapshot, new ProviderStatus(authentication, freshness, now, snapshot is null ? null : now, error?.RetryAt, scenario == MockScenario.Loading, error), activity);
    }

    private static UsageSnapshot CreateSnapshot(ProviderId provider, DateTimeOffset now, DataFreshness freshness, MockScenario scenario)
    {
        (decimal? used, decimal? remaining, decimal? capacity, decimal? fraction) = scenario switch
        {
            MockScenario.ZeroUsage => (0m, 5m, 5m, 0m),
            MockScenario.FullUsage => (5m, 0m, 5m, 1m),
            MockScenario.OverLimit => (6m, (decimal?)null, 5m, 1.2m),
            _ => (2m, 3m, 5m, 0.4m),
        };
        var reset = scenario == MockScenario.ResetPassed ? now.AddMinutes(-30) : now.AddHours(4);
        var window = new QuotaWindow("session", "Codex session", TimeSpan.FromHours(5), now.AddHours(-1), reset, new UsageLimit(used, remaining, capacity, fraction, "requests"));
        var headline = scenario == MockScenario.ResetPassed ? null : "session";
        return new UsageSnapshot(provider, new AccountScope("mock-account", IdentityConfidence.ProviderConfirmed), freshness == DataFreshness.Stale ? now.AddMinutes(-20) : now, [window], headline, new SourceDescriptor("mock", "M04", false), null);
    }
}

public sealed class MockUsageProvider(MockScenario scenario, ProviderId provider = ProviderId.OpenAi, TimeProvider? timeProvider = null) : IUsageProvider
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    public ProviderId Provider => provider;

    public Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(MockScenarioCatalog.Create(provider, scenario, _timeProvider).Snapshot);
    }
}
