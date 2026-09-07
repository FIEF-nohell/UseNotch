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

public enum MockScenario { Loading, Authenticated, Missing, Expired, Rejected, Forbidden, Unsupported, Stale, ResetPassed, RateLimited, Error, Working, Waiting, Estimated, UnknownActivity }

public sealed record MockProviderState(UsageSnapshot? Snapshot, ProviderStatus Status, ActivitySession? Activity);

public static class MockScenarioCatalog
{
    public static MockProviderState Create(MockScenario scenario, TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var now = clock.GetUtcNow();
        var authentication = scenario switch
        {
            MockScenario.Authenticated or MockScenario.Stale or MockScenario.ResetPassed or MockScenario.Working or MockScenario.Waiting or MockScenario.Estimated => AuthenticationState.Authenticated,
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
            _ => ActivityState.Unknown,
        };
        var activity = scenario is MockScenario.Working or MockScenario.Waiting or MockScenario.Estimated
            ? new ActivitySession("mock-session", ProviderId.OpenAi, activityState, now.AddMinutes(-2), now, scenario == MockScenario.Estimated ? ReadingFidelity.Derived : ReadingFidelity.ProviderReported, null)
            : null;
        var snapshot = scenario is MockScenario.Authenticated or MockScenario.Stale or MockScenario.ResetPassed or MockScenario.Working or MockScenario.Waiting or MockScenario.Estimated
            ? CreateSnapshot(now, freshness)
            : null;
        return new MockProviderState(snapshot, new ProviderStatus(authentication, freshness, now, snapshot is null ? null : now, error?.RetryAt, scenario == MockScenario.Loading, error), activity);
    }

    private static UsageSnapshot CreateSnapshot(DateTimeOffset now, DataFreshness freshness)
    {
        var window = new QuotaWindow("session", "Codex session", TimeSpan.FromHours(5), now.AddHours(-1), now.AddHours(4), new UsageLimit(2, 3, 5, 0.4m, "requests"));
        return new UsageSnapshot(ProviderId.OpenAi, new AccountScope("mock-account", IdentityConfidence.ProviderConfirmed), freshness == DataFreshness.Stale ? now.AddMinutes(-20) : now, [window], "session", new SourceDescriptor("mock", "M04", false), null);
    }
}

public sealed class MockUsageProvider(MockScenario scenario, TimeProvider? timeProvider = null) : IUsageProvider
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    public ProviderId Provider => ProviderId.OpenAi;

    public Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(MockScenarioCatalog.Create(scenario, _timeProvider).Snapshot);
    }
}
