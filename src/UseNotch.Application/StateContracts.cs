using UseNotch.Domain;

namespace UseNotch.Application;

public enum RefreshReason { Timer, Manual, Resume, SourceChanged }

public sealed record ProviderRuntimeState(
    ProviderConnection Connection,
    UsageSnapshot? Snapshot,
    ProviderStatus Status,
    long CredentialGeneration,
    long AccountGeneration)
{
    public ProviderRuntimeState ForConnection(ProviderConnection connection) => this with { Connection = connection };
}

public interface IUsageStateStore
{
    ProviderRuntimeState? Get(ProviderId provider);
    bool TryPublish(ProviderRuntimeState candidate);
    void Disconnect(ProviderId provider);
    void Clear();
}

public sealed class InMemoryUsageStateStore : IUsageStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ProviderId, ProviderRuntimeState> _states = [];

    public ProviderRuntimeState? Get(ProviderId provider)
    {
        lock (_gate)
        {
            return _states.GetValueOrDefault(provider);
        }
    }

    public bool TryPublish(ProviderRuntimeState candidate)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(candidate.Connection.Provider, out var current)
                && (candidate.Connection.Generation < current.Connection.Generation
                    || candidate.CredentialGeneration < current.CredentialGeneration
                    || candidate.AccountGeneration < current.AccountGeneration))
            {
                return false;
            }

            _states[candidate.Connection.Provider] = candidate;
            return true;
        }
    }

    public void Disconnect(ProviderId provider)
    {
        lock (_gate)
        {
            _states.Remove(provider);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _states.Clear();
        }
    }
}

public interface IUiDispatcher
{
    ValueTask DispatchAsync(Action update, CancellationToken cancellationToken);
}

public sealed class InlineUiDispatcher : IUiDispatcher
{
    public ValueTask DispatchAsync(Action update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        update();
        return ValueTask.CompletedTask;
    }
}

public sealed record PollingOptions(
    TimeSpan ActiveInterval,
    TimeSpan IdleInterval,
    TimeSpan AttemptBudget,
    TimeSpan OperationBudget)
{
    public static PollingOptions Default { get; } = new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(35));
}

public sealed class PollingCoordinator : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<ProviderId, IUsageProvider> _providers;
    private readonly IUsageStateStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly PollingOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<ProviderId, Worker> _workers = [];
    private bool _paused;
    private bool _disposed;

    public PollingCoordinator(
        IEnumerable<IUsageProvider> providers,
        IUsageStateStore store,
        IUiDispatcher? dispatcher = null,
        PollingOptions? options = null)
    {
        _providers = providers.ToDictionary(provider => provider.Provider);
        _store = store;
        _dispatcher = dispatcher ?? new InlineUiDispatcher();
        _options = options ?? PollingOptions.Default;
    }

    public void Start(ProviderConnection connection)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_providers.ContainsKey(connection.Provider))
            {
                throw new InvalidOperationException($"No provider registered for {connection.Provider}.");
            }

            if (_workers.ContainsKey(connection.Provider))
            {
                return;
            }

            var worker = new Worker(this, connection);
            _workers.Add(connection.Provider, worker);
            worker.Start();
        }
    }

    public void RequestRefresh(ProviderId provider, RefreshReason reason)
    {
        lock (_gate)
        {
            if (!_paused && _workers.TryGetValue(provider, out var worker))
            {
                worker.Request(reason);
            }
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            _paused = paused;
            foreach (var worker in _workers.Values)
            {
                worker.SetPaused(paused);
            }
        }
    }

    public async ValueTask DisconnectAsync(ProviderId provider)
    {
        Worker? worker;
        lock (_gate)
        {
            _workers.Remove(provider, out worker);
        }

        if (worker is not null)
        {
            await worker.DisposeAsync();
        }

        _store.Disconnect(provider);
    }

    public async ValueTask DisposeAsync()
    {
        Worker[] workers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            workers = _workers.Values.ToArray();
            _workers.Clear();
        }
        foreach (var worker in workers)
        {
            await worker.DisposeAsync();
        }
    }

    private sealed class Worker : IAsyncDisposable
    {
        private readonly PollingCoordinator _owner;
        private readonly ProviderConnection _connection;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly object _gate = new();
        private Task? _loop;
        private bool _requested;
        private bool _paused;

        public Worker(PollingCoordinator owner, ProviderConnection connection)
        {
            _owner = owner;
            _connection = connection;
        }

        public void Start() => _loop = Task.Run(RunAsync);

        public void Request(RefreshReason reason)
        {
            lock (_gate)
            {
                if (_requested)
                {
                    return;
                }

                _requested = true;
                _signal.Release();
            }
        }

        public void SetPaused(bool paused)
        {
            lock (_gate)
            {
                _paused = paused;
            }

            if (!paused)
            {
                Request(RefreshReason.Resume);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_loop is not null)
            {
                await _loop;
            }

            _signal.Dispose();
            _stop.Dispose();
        }

        private async Task RunAsync()
        {
            Request(RefreshReason.Timer);
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await _signal.WaitAsync(_stop.Token);
                    lock (_gate)
                    {
                        _requested = false;
                    }

                    if (_paused)
                    {
                        continue;
                    }

                    await ReadOnceAsync();
                    await Task.Delay(_owner._options.ActiveInterval, _stop.Token);
                    Request(RefreshReason.Timer);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task ReadOnceAsync()
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            operation.CancelAfter(_owner._options.OperationBudget);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            attempt.CancelAfter(_owner._options.AttemptBudget);
            var snapshot = await _owner._providers[_connection.Provider].ReadAsync(_connection, attempt.Token);
            if (snapshot is null)
            {
                return;
            }

            var current = _owner._store.Get(_connection.Provider);
            var candidate = new ProviderRuntimeState(
                _connection,
                snapshot,
                new ProviderStatus(AuthenticationState.Authenticated, DataFreshness.Fresh, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false, null),
                current?.CredentialGeneration ?? 0,
                current?.AccountGeneration ?? 0);
            if (_owner._store.TryPublish(candidate))
            {
                await _owner._dispatcher.DispatchAsync(() => { }, _stop.Token);
            }
        }
    }
}
