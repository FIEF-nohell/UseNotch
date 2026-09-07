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
}
