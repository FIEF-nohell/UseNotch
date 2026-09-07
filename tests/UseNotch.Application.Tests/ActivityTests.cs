using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.Application.Tests;

public class ActivityAggregatorTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static ActivitySession Session(ActivityState state, ReadingFidelity fidelity, TimeSpan age, string id = "session")
        => new(id, ProviderId.Anthropic, state, Now - age, Now - age, fidelity, null);

    [Fact]
    public void Estimated_observations_expire_while_reported_ones_do_not()
    {
        var expired = Session(ActivityState.Working, ReadingFidelity.Derived, TimeSpan.FromSeconds(9), "stale-estimate");
        var reported = Session(ActivityState.Idle, ReadingFidelity.ProviderReported, TimeSpan.FromMinutes(20), "reported");

        var summary = ActivityAggregator.Summarize([expired, reported], Now, TimeSpan.FromSeconds(8));

        Assert.Equal("reported", summary!.Id);
    }

    [Fact]
    public void Expiring_the_only_estimate_leaves_no_session_rather_than_an_idle_one()
    {
        var expired = Session(ActivityState.Working, ReadingFidelity.Derived, TimeSpan.FromSeconds(9));

        Assert.Null(ActivityAggregator.Summarize([expired], Now, TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public void Reported_waiting_wins_over_working()
    {
        var working = Session(ActivityState.Working, ReadingFidelity.ProviderReported, TimeSpan.Zero, "working");
        var waiting = Session(ActivityState.Waiting, ReadingFidelity.ProviderReported, TimeSpan.FromSeconds(1), "waiting");

        var summary = ActivityAggregator.Summarize([working, waiting], Now, TimeSpan.FromSeconds(8));

        Assert.Equal("waiting", summary!.Id);
    }

    [Fact]
    public void An_estimated_waiting_never_outranks_a_reported_working_state()
    {
        var working = Session(ActivityState.Working, ReadingFidelity.ProviderReported, TimeSpan.Zero, "working");
        var guessedWaiting = Session(ActivityState.Waiting, ReadingFidelity.Derived, TimeSpan.Zero, "guessed");

        var summary = ActivityAggregator.Summarize([working, guessedWaiting], Now, TimeSpan.FromSeconds(8));

        Assert.Equal("working", summary!.Id);
    }

    [Fact]
    public void Unknown_never_outranks_a_known_state_and_an_empty_set_stays_empty()
    {
        var unknown = Session(ActivityState.Unknown, ReadingFidelity.ProviderReported, TimeSpan.Zero, "unknown");
        var idle = Session(ActivityState.Idle, ReadingFidelity.ProviderReported, TimeSpan.FromSeconds(30), "idle");

        Assert.Equal("idle", ActivityAggregator.Summarize([unknown, idle], Now, TimeSpan.FromSeconds(8))!.Id);
        Assert.Null(ActivityAggregator.Summarize([], Now, TimeSpan.FromSeconds(8)));
    }
}

public class ActivityStoreTests
{
    private static ProviderRuntimeState QuotaState(long generation = 1)
    {
        var now = DateTimeOffset.UtcNow;
        var window = new QuotaWindow("session", "session", TimeSpan.FromHours(5), now, now.AddHours(4), new UsageLimit(1, 4, 5, .2m, "requests"));
        var snapshot = new UsageSnapshot(ProviderId.OpenAi, new AccountScope("account", IdentityConfidence.LocalPartition), now, [window], "session", new SourceDescriptor("test", "activity", true), null);
        return new ProviderRuntimeState(
            new ProviderConnection(ProviderId.OpenAi, "mock", true, generation),
            snapshot,
            new ProviderStatus(AuthenticationState.Authenticated, DataFreshness.Fresh, now, now, null, false, null),
            0,
            0);
    }

    private static ActivityReading Reading(ActivityState state)
    {
        var now = DateTimeOffset.UtcNow;
        return ActivityReading.Supported(ProviderId.OpenAi, now, new ActivitySession("s", ProviderId.OpenAi, state, now, now, ReadingFidelity.ProviderReported, null), 1);
    }

    [Fact]
    public void A_quota_publish_does_not_drop_a_live_activity_reading()
    {
        var store = new InMemoryUsageStateStore();
        store.TryPublish(QuotaState());
        store.TryPublishActivity(ProviderId.OpenAi, Reading(ActivityState.Working));

        store.TryPublish(QuotaState(generation: 2));

        Assert.Equal(ActivityState.Working, store.Get(ProviderId.OpenAi)!.Activity!.Session!.State);
    }

    [Fact]
    public void Activity_published_before_any_quota_state_is_reported_as_not_yet_attached()
    {
        var store = new InMemoryUsageStateStore();

        Assert.False(store.TryPublishActivity(ProviderId.OpenAi, Reading(ActivityState.Working)));
        Assert.Null(store.Get(ProviderId.OpenAi));

        store.TryPublish(QuotaState());
        Assert.Equal(ActivityState.Working, store.Get(ProviderId.OpenAi)!.Activity!.Session!.State);
    }

    [Fact]
    public void Disconnecting_clears_activity_with_the_quota_state()
    {
        var store = new InMemoryUsageStateStore();
        store.TryPublish(QuotaState());
        store.TryPublishActivity(ProviderId.OpenAi, Reading(ActivityState.Working));

        store.Disconnect(ProviderId.OpenAi);
        store.TryPublish(QuotaState());

        Assert.Null(store.Get(ProviderId.OpenAi)!.Activity);
    }
}

public class ActivityCoordinatorTests
{
    private sealed class StubMonitor(ProviderId provider, Func<int, ActivityReading> readings) : IActivityMonitor
    {
        private int _calls;

        public ProviderId Provider => provider;
        public int Calls => Volatile.Read(ref _calls);

        public Task<ActivityReading> ObserveAsync(CancellationToken cancellationToken)
            => Task.FromResult(readings(Interlocked.Increment(ref _calls)));
    }

    private static ProviderRuntimeState QuotaState(ProviderId provider)
    {
        var now = DateTimeOffset.UtcNow;
        return new ProviderRuntimeState(
            new ProviderConnection(provider, "mock", true, 1),
            null,
            new ProviderStatus(AuthenticationState.Authenticated, DataFreshness.Fresh, now, now, null, false, null),
            0,
            0);
    }

    private static ActivityOptions FastOptions => new(
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromMilliseconds(5),
        TimeSpan.FromSeconds(1));

    private static async Task<ActivityReading> WaitForActivityAsync(InMemoryUsageStateStore store, ProviderId provider)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (store.Get(provider)?.Activity is { } activity)
            {
                return activity;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("No activity reading was published.");
    }

    [Fact]
    public async Task A_monitor_that_throws_reports_unavailable_activity_without_leaking_its_message()
    {
        var store = new InMemoryUsageStateStore();
        store.TryPublish(QuotaState(ProviderId.OpenAi));
        var monitor = new StubMonitor(ProviderId.OpenAi, _ => throw new InvalidOperationException("C:\\Users\\someone\\secret-project"));
        await using var coordinator = new ActivityCoordinator([monitor], store, options: FastOptions);

        coordinator.Start(ProviderId.OpenAi);
        var reading = await WaitForActivityAsync(store, ProviderId.OpenAi);

        Assert.Equal(ActivityCapability.Unsupported, reading.Capability);
        Assert.Equal("Activity unavailable", reading.UnsupportedReason);
        Assert.Null(reading.Session);
    }

    [Fact]
    public async Task A_failing_activity_monitor_leaves_the_quota_state_untouched()
    {
        var store = new InMemoryUsageStateStore();
        store.TryPublish(QuotaState(ProviderId.OpenAi));
        var monitor = new StubMonitor(ProviderId.OpenAi, _ => throw new InvalidOperationException("boom"));
        await using var coordinator = new ActivityCoordinator([monitor], store, options: FastOptions);

        coordinator.Start(ProviderId.OpenAi);
        await WaitForActivityAsync(store, ProviderId.OpenAi);

        var state = store.Get(ProviderId.OpenAi)!;
        Assert.Equal(AuthenticationState.Authenticated, state.Status.Authentication);
        Assert.Null(state.Status.Error);
    }

    [Fact]
    public async Task Pausing_stops_observation_and_disconnecting_clears_the_reading()
    {
        var store = new InMemoryUsageStateStore();
        store.TryPublish(QuotaState(ProviderId.Anthropic));
        var now = DateTimeOffset.UtcNow;
        var monitor = new StubMonitor(
            ProviderId.Anthropic,
            _ => ActivityReading.Supported(ProviderId.Anthropic, now, new ActivitySession("s", ProviderId.Anthropic, ActivityState.Working, now, now, ReadingFidelity.ProviderReported, null), 1));
        await using var coordinator = new ActivityCoordinator([monitor], store, options: FastOptions);

        coordinator.Start(ProviderId.Anthropic);
        await WaitForActivityAsync(store, ProviderId.Anthropic);

        coordinator.SetPaused(true);
        await Task.Delay(60);
        var callsWhilePaused = monitor.Calls;
        await Task.Delay(120);
        Assert.Equal(callsWhilePaused, monitor.Calls);

        await coordinator.DisconnectAsync(ProviderId.Anthropic);
        Assert.Null(store.Get(ProviderId.Anthropic)!.Activity);
    }
}
