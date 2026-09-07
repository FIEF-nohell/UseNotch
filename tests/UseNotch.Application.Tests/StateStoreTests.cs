using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.Application.Tests;

public class StateStoreTests
{
    [Fact]
    public void Older_connection_generation_cannot_resurrect_state()
    {
        var store = new InMemoryUsageStateStore();
        var current = State(2, 2, 2);
        var old = State(1, 2, 2);

        Assert.True(store.TryPublish(current));
        Assert.False(store.TryPublish(old));
        Assert.Equal(2, store.Get(ProviderId.OpenAi)!.Connection.Generation);
    }

    [Fact]
    public void Older_credential_or_account_generation_is_rejected()
    {
        var store = new InMemoryUsageStateStore();
        var current = State(1, 4, 3);

        Assert.True(store.TryPublish(current));
        Assert.False(store.TryPublish(State(1, 3, 3)));
        Assert.False(store.TryPublish(State(1, 4, 2)));
    }

    [Fact]
    public void Disconnect_removes_only_the_selected_provider()
    {
        var store = new InMemoryUsageStateStore();
        store.TryPublish(State(1, 1, 1));
        store.TryPublish(State(1, 1, 1) with { Connection = new ProviderConnection(ProviderId.Anthropic, "mock", true, 1) });

        store.Disconnect(ProviderId.OpenAi);

        Assert.Null(store.Get(ProviderId.OpenAi));
        Assert.NotNull(store.Get(ProviderId.Anthropic));
    }

    private static ProviderRuntimeState State(long connection, long credential, long account) =>
        new(new ProviderConnection(ProviderId.OpenAi, "mock", true, connection), null,
            new ProviderStatus(AuthenticationState.Discovering, DataFreshness.Unknown, null, null, null, false, null), credential, account);
}
