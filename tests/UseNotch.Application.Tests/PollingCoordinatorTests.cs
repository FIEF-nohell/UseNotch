using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.Application.Tests;

public class PollingCoordinatorTests
{
    [Fact]
    public async Task Repeated_refresh_requests_keep_one_read_in_flight()
    {
        var provider = new BlockingProvider();
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator(
            [provider], store, options: new PollingOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));

        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        coordinator.RequestRefresh(ProviderId.OpenAi, RefreshReason.Manual);
        coordinator.RequestRefresh(ProviderId.OpenAi, RefreshReason.Resume);
        Assert.Equal(1, provider.Calls);

        provider.Release.TrySetResult(true);
        await Task.Delay(50);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Disconnect_cancels_owned_work_and_clears_state()
    {
        var provider = new BlockingProvider();
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator([provider], store);
        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await coordinator.DisconnectAsync(ProviderId.OpenAi);

        Assert.True(provider.Cancelled);
        Assert.Null(store.Get(ProviderId.OpenAi));
    }

    [Fact]
    public async Task Disabled_provider_does_not_start_a_worker()
    {
        var provider = new CountingProvider();
        await using var coordinator = new PollingCoordinator([provider], new InMemoryUsageStateStore());

        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", false, 1));
        await Task.Delay(100);

        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Idle_mode_uses_the_idle_interval()
    {
        var provider = new CadenceProvider();
        await using var coordinator = new PollingCoordinator(
            [provider],
            new InMemoryUsageStateStore(),
            options: new PollingOptions(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        coordinator.SetIdle(ProviderId.OpenAi, true);
        provider.Release.TrySetResult(true);

        await provider.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(provider.Calls >= 2);
    }

    [Fact]
    public async Task Failure_publishes_a_safe_error_and_next_attempt()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        var provider = new FailingProvider(new ProviderReadException("secret response", true, serverDeadline: deadline, category: ErrorCategory.RateLimited, code: 429));
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator(
            [provider],
            store,
            options: new PollingOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));

        var state = await WaitForStateAsync(store, ProviderId.OpenAi);
        Assert.Equal(ErrorCategory.RateLimited, state.Status.Error!.Category);
        Assert.Equal("Provider rate limit reached", state.Status.Error.SafeMessage);
        Assert.True(state.Status.NextAttempt >= deadline);
    }

    [Fact]
    public async Task Rate_limit_deadline_is_persisted_for_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "usenotch-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            var provider = new FailingProvider(new ProviderReadException("secret response", true, serverDeadline: deadline, category: ErrorCategory.RateLimited, code: 429));
            var store = new InMemoryUsageStateStore();
            await using var coordinator = new PollingCoordinator(
                [provider],
                store,
                options: new PollingOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
                cache: new JsonUsageCache(root));
            coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));

            var state = await WaitForStateAsync(store, ProviderId.OpenAi);
            ProviderRuntimeState? restored = null;
            for (var attempt = 0; attempt < 100 && restored is null; attempt++)
            {
                restored = await new JsonUsageCache(root).LoadAsync(ProviderId.OpenAi, CancellationToken.None);
                if (restored is null)
                {
                    await Task.Delay(10);
                }
            }
            Assert.Equal(state.Status.NextAttempt, restored!.Status.NextAttempt);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Non_transient_failure_waits_for_an_explicit_refresh()
    {
        var provider = new FailingProvider(new ProviderReadException("secret response", false, category: ErrorCategory.Authentication));
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator([provider], store);
        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));

        var state = await WaitForStateAsync(store, ProviderId.OpenAi);
        await Task.Delay(100);
        Assert.Equal(1, provider.Calls);
        Assert.Null(state.Status.NextAttempt);
        Assert.False(state.Status.Error!.Retryable);
    }

    [Fact]
    public async Task Published_states_are_sent_through_the_ui_dispatcher()
    {
        var provider = new SnapshotProvider();
        var dispatched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new RecordingDispatcher(dispatched);
        await using var coordinator = new PollingCoordinator(
            [provider],
            new InMemoryUsageStateStore(),
            dispatcher,
            new PollingOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));

        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task Late_response_after_disconnect_is_not_published()
    {
        var provider = new LateProvider();
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator([provider], store);
        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var disconnect = coordinator.DisconnectAsync(ProviderId.OpenAi).AsTask();
        await Task.Delay(20);
        provider.Release.TrySetResult(true);
        await disconnect;

        Assert.Null(store.Get(ProviderId.OpenAi));
    }

    [Fact]
    public async Task Failure_authentication_hint_overrides_the_previous_state()
    {
        var provider = new FailingProvider(new ProviderReadException("secret response", false, category: ErrorCategory.Authentication, code: 401, authenticationHint: AuthenticationState.Expired));
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator([provider], store);
        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));

        var state = await WaitForStateAsync(store, ProviderId.OpenAi);
        Assert.Equal(AuthenticationState.Expired, state.Status.Authentication);
    }

    [Fact]
    public async Task Failure_without_a_hint_keeps_the_previously_observed_authentication_state()
    {
        var provider = new SequencedProvider(
            _ => Task.FromResult<UsageSnapshot?>(Snapshot("account-a")),
            _ => Task.FromException<UsageSnapshot?>(new ProviderReadException("secret response", true, category: ErrorCategory.Network)));
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator(
            [provider],
            store,
            options: new PollingOptions(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));

        ProviderRuntimeState? state = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            state = store.Get(ProviderId.OpenAi);
            if (state?.Status.Error is not null)
            {
                break;
            }
            await Task.Delay(10);
        }

        Assert.Equal(AuthenticationState.Authenticated, state!.Status.Authentication);
    }

    [Fact]
    public async Task Account_change_starts_a_new_epoch_and_replaces_the_old_reading()
    {
        var provider = new SequencedProvider(
            _ => Task.FromResult<UsageSnapshot?>(Snapshot("account-a")),
            _ => Task.FromResult<UsageSnapshot?>(Snapshot("account-b")));
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator(
            [provider],
            store,
            options: new PollingOptions(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));

        var first = await WaitForStateAsync(store, ProviderId.OpenAi);
        Assert.Equal("account-a", first.Snapshot!.Account.Partition);
        Assert.Equal(0, first.AccountGeneration);

        ProviderRuntimeState second = first;
        for (var attempt = 0; attempt < 200 && second.Snapshot!.Account.Partition == "account-a"; attempt++)
        {
            await Task.Delay(10);
            second = store.Get(ProviderId.OpenAi)!;
        }

        Assert.Equal("account-b", second.Snapshot!.Account.Partition);
        Assert.Equal(1, second.AccountGeneration);
    }

    private static UsageSnapshot Snapshot(string accountPartition)
    {
        var now = DateTimeOffset.UtcNow;
        var window = new QuotaWindow("session", "session", TimeSpan.FromHours(5), now, now.AddHours(4), new UsageLimit(1, 4, 5, .2m, "requests"));
        return new UsageSnapshot(ProviderId.OpenAi, new AccountScope(accountPartition, IdentityConfidence.LocalPartition), now, [window], "session", new SourceDescriptor("test", "epoch", true), null);
    }

    [Fact]
    public async Task Cached_startup_is_stale_and_expired_headlines_are_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), "usenotch-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var window = new QuotaWindow("session", "session", TimeSpan.FromHours(5), now.AddDays(-2), now.AddDays(-1), new UsageLimit(1, 4, 5, .2m, "requests"));
            var snapshot = new UsageSnapshot(ProviderId.OpenAi, new AccountScope("account", IdentityConfidence.ProviderConfirmed), now.AddDays(-1), [window], "session", new SourceDescriptor("test", "cache", true), null);
            var cached = new ProviderRuntimeState(
                new ProviderConnection(ProviderId.OpenAi, "mock", true, 1),
                snapshot,
                new ProviderStatus(AuthenticationState.Authenticated, DataFreshness.Fresh, now.AddDays(-1), now.AddDays(-1), null, false, null),
                1,
                1);
            var cache = new JsonUsageCache(root);
            await cache.SaveAsync(cached, CancellationToken.None);
            var provider = new BlockingProvider();
            var store = new InMemoryUsageStateStore();
            await using var coordinator = new PollingCoordinator([provider], store, cache: cache);

            await coordinator.StartAsync(new ProviderConnection(ProviderId.OpenAi, "mock", true, 2));
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var restored = store.Get(ProviderId.OpenAi)!;
            Assert.Equal(StateOrigin.CachedStartup, restored.Origin);
            Assert.Equal(DataFreshness.Expired, restored.Status.Freshness);
            Assert.Null(restored.Snapshot!.Headline);
            provider.Release.TrySetResult(true);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task<ProviderRuntimeState> WaitForStateAsync(InMemoryUsageStateStore store, ProviderId provider)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (store.Get(provider) is { } state)
            {
                return state;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("The provider state was not published.");
    }

    private sealed class BlockingProvider : IUsageProvider
    {
        public ProviderId Provider => ProviderId.OpenAi;
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool Cancelled { get; private set; }

        public async Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult(true);
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return null;
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }
    }

    private sealed class CountingProvider : IUsageProvider
    {
        public ProviderId Provider => ProviderId.OpenAi;
        public int Calls { get; private set; }
        public Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<UsageSnapshot?>(null);
        }
    }

    private sealed class CadenceProvider : IUsageProvider
    {
        public ProviderId Provider => ProviderId.OpenAi;
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public async Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
            {
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }
            else
            {
                SecondCall.TrySetResult(true);
            }
            return null;
        }
    }

    private sealed class FailingProvider(ProviderReadException exception) : IUsageProvider
    {
        public ProviderId Provider => ProviderId.OpenAi;
        public int Calls { get; private set; }

        public Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromException<UsageSnapshot?>(exception);
        }
    }

    private sealed class SnapshotProvider : IUsageProvider
    {
        public ProviderId Provider => ProviderId.OpenAi;

        public Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var window = new QuotaWindow("session", "session", TimeSpan.FromHours(5), now, now.AddHours(4), new UsageLimit(1, 4, 5, .2m, "requests"));
            return Task.FromResult<UsageSnapshot?>(new UsageSnapshot(ProviderId.OpenAi, new AccountScope("account", IdentityConfidence.ProviderConfirmed), now, [window], "session", new SourceDescriptor("test", "dispatcher", true), null));
        }
    }

    private sealed class SequencedProvider(params Func<ProviderConnection, Task<UsageSnapshot?>>[] calls) : IUsageProvider
    {
        private int _index;
        public ProviderId Provider => ProviderId.OpenAi;

        public Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken)
        {
            var index = Math.Min(_index, calls.Length - 1);
            _index++;
            return calls[index](connection);
        }
    }

    private sealed class RecordingDispatcher(TaskCompletionSource<bool> completed) : IUiDispatcher
    {
        public int Calls { get; private set; }

        public ValueTask DispatchAsync(Action update, CancellationToken cancellationToken)
        {
            Calls++;
            update();
            completed.TrySetResult(true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LateProvider : IUsageProvider
    {
        public ProviderId Provider => ProviderId.OpenAi;
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            await Release.Task;
            return null;
        }
    }
}
