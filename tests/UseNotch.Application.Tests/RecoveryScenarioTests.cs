using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.Application.Tests;

/// <summary>
/// The failure and recovery paths the product has to survive in real use: a long server deadline, a
/// schema change, an account switch, a disconnect and reconnect, and one provider failing while the
/// other keeps working.
/// </summary>
public class RecoveryScenarioTests
{
    private static PollingOptions FastOptions => new(
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2));

    private static UsageSnapshot Snapshot(ProviderId provider, string account = "account-a")
    {
        var now = DateTimeOffset.UtcNow;
        var window = new QuotaWindow("session", "5h limit", TimeSpan.FromHours(5), now, now.AddHours(4), new UsageLimit(1, 4, 5, .2m, "requests"));
        return new UsageSnapshot(provider, new AccountScope(account, IdentityConfidence.LocalPartition), now, [window], "session", new SourceDescriptor("test", "m13", true), null);
    }

    private static async Task<ProviderRuntimeState> WaitForAsync(
        InMemoryUsageStateStore store,
        ProviderId provider,
        Func<ProviderRuntimeState, bool> condition,
        int attempts = 300)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (store.Get(provider) is { } state && condition(state))
            {
                return state;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"The expected {provider} state never appeared.");
    }

    [Fact]
    public async Task A_long_server_deadline_is_honoured_exactly_and_never_shortened()
    {
        var deadline = DateTimeOffset.UtcNow.AddHours(3);
        var provider = new ScriptedProvider(ProviderId.OpenAi, _ => throw new ProviderReadException(
            "Provider rate limit reached",
            true,
            serverDeadline: deadline,
            category: ErrorCategory.RateLimited,
            code: 429));
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator([provider], store, options: FastOptions);

        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));
        var state = await WaitForAsync(store, ProviderId.OpenAi, candidate => candidate.Status.Error is not null);

        // Local backoff may only ever extend a server deadline, never bring it forward.
        Assert.Equal(deadline, state.Status.NextAttempt);
        Assert.Equal(ErrorCategory.RateLimited, state.Status.Error!.Category);

        await Task.Delay(100);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task A_schema_failure_backs_off_further_than_a_single_transient_failure()
    {
        var now = DateTimeOffset.UtcNow;

        var transient = BackoffPolicy.NextAttempt(now, 1, null);
        var schema = BackoffPolicy.SchemaNextAttempt(now, 1);

        Assert.True(schema >= transient);
        Assert.True(BackoffPolicy.SchemaNextAttempt(now, 3) > BackoffPolicy.SchemaNextAttempt(now, 1));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_schema_failure_is_published_safely_and_keeps_retrying_on_its_own_schedule()
    {
        var provider = new ScriptedProvider(ProviderId.OpenAi, _ => throw new ProviderReadException(
            "Provider response format is unsupported",
            false,
            schemaFailure: true));
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator([provider], store, options: FastOptions);

        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));
        var state = await WaitForAsync(store, ProviderId.OpenAi, candidate => candidate.Status.Error is not null);

        Assert.Equal(ErrorCategory.Schema, state.Status.Error!.Category);
        Assert.Equal("Provider response format is unsupported", state.Status.Error.SafeMessage);
        Assert.NotNull(state.Status.NextAttempt);
    }

    [Fact]
    public async Task Reconnecting_after_a_disconnect_publishes_a_fresh_reading()
    {
        var provider = new ScriptedProvider(ProviderId.OpenAi, _ => Task.FromResult<UsageSnapshot?>(Snapshot(ProviderId.OpenAi)));
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator([provider], store, options: FastOptions);

        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));
        await WaitForAsync(store, ProviderId.OpenAi, candidate => candidate.Snapshot is not null);

        await coordinator.DisconnectAsync(ProviderId.OpenAi);
        Assert.Null(store.Get(ProviderId.OpenAi));

        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 2));
        var reconnected = await WaitForAsync(store, ProviderId.OpenAi, candidate => candidate.Snapshot is not null);

        Assert.Equal(2, reconnected.Connection.Generation);
        Assert.Equal(StateOrigin.Live, reconnected.Origin);
    }

    [Fact]
    public async Task One_provider_failing_does_not_stop_the_other_from_recovering()
    {
        var failing = new ScriptedProvider(ProviderId.Anthropic, _ => throw new ProviderReadException("Provider request failed", true));
        var working = new ScriptedProvider(ProviderId.OpenAi, _ => Task.FromResult<UsageSnapshot?>(Snapshot(ProviderId.OpenAi)));
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator([failing, working], store, options: FastOptions);

        coordinator.Start(new ProviderConnection(ProviderId.Anthropic, "mock", true, 1));
        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));

        var anthropic = await WaitForAsync(store, ProviderId.Anthropic, candidate => candidate.Status.Error is not null);
        var openAi = await WaitForAsync(store, ProviderId.OpenAi, candidate => candidate.Snapshot is not null);

        Assert.NotNull(anthropic.Status.Error);
        Assert.Null(anthropic.Snapshot);
        Assert.NotNull(openAi.Snapshot);
        Assert.Null(openAi.Status.Error);
    }

    [Fact]
    public async Task An_account_switch_replaces_the_reading_and_starts_a_new_epoch()
    {
        var allowSecondAccount = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new ScriptedProvider(ProviderId.OpenAi, async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return Snapshot(ProviderId.OpenAi, "account-a");
            }

            await allowSecondAccount.Task;
            return Snapshot(ProviderId.OpenAi, "account-b");
        });
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator([provider], store, options: FastOptions);

        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));
        var first = await WaitForAsync(store, ProviderId.OpenAi, candidate => candidate.Snapshot?.Account.Partition == "account-a");
        Assert.Equal(0, first.AccountGeneration);

        allowSecondAccount.SetResult(true);
        var second = await WaitForAsync(store, ProviderId.OpenAi, candidate => candidate.Snapshot?.Account.Partition == "account-b");

        Assert.Equal(1, second.AccountGeneration);
    }

    [Fact]
    public async Task Shutdown_during_a_pending_operation_completes_without_leaving_work_behind()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var provider = new ScriptedProvider(ProviderId.OpenAi, async token =>
        {
            entered.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }

            return null;
        });
        var store = new InMemoryUsageStateStore();
        var coordinator = new PollingCoordinator([provider], store, options: FastOptions);

        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancelled);
    }

    [Fact]
    public async Task Pausing_stops_requests_and_resuming_starts_them_again()
    {
        var provider = new ScriptedProvider(ProviderId.OpenAi, _ => Task.FromResult<UsageSnapshot?>(Snapshot(ProviderId.OpenAi)));
        var store = new InMemoryUsageStateStore();
        await using var coordinator = new PollingCoordinator([provider], store, options: FastOptions);
        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "mock", true, 1));
        await WaitForAsync(store, ProviderId.OpenAi, candidate => candidate.Snapshot is not null);

        coordinator.SetPaused(true);
        await Task.Delay(80);
        var callsWhilePaused = provider.Calls;
        await Task.Delay(120);
        Assert.Equal(callsWhilePaused, provider.Calls);

        coordinator.SetPaused(false);
        for (var attempt = 0; attempt < 200 && provider.Calls == callsWhilePaused; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(provider.Calls > callsWhilePaused);
    }

    private sealed class ScriptedProvider : IUsageProvider
    {
        private readonly Func<CancellationToken, Task<UsageSnapshot?>> _read;
        private int _calls;

        public ScriptedProvider(ProviderId provider, Func<CancellationToken, Task<UsageSnapshot?>> read)
        {
            Provider = provider;
            _read = read;
        }

        public ProviderId Provider { get; }

        public int Calls => Volatile.Read(ref _calls);

        public Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _read(cancellationToken);
        }
    }
}
