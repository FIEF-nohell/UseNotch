using UseNotch.Domain;

namespace UseNotch.Application;

public enum RefreshReason { Timer, Manual, Resume, SourceChanged }
public enum StateOrigin { Live, CachedStartup }

public sealed record ProviderRuntimeState(
    ProviderConnection Connection,
    UsageSnapshot? Snapshot,
    ProviderStatus Status,
    long CredentialGeneration,
    long AccountGeneration)
{
    public StateOrigin Origin { get; init; } = StateOrigin.Live;

    /// <summary>
    /// Live activity for this provider. It is owned by the activity workers, is never restored from the
    /// quota cache, and stays null when a provider reports no activity capability yet.
    /// </summary>
    public ActivityReading? Activity { get; init; }

    public ProviderRuntimeState ForConnection(ProviderConnection connection) => this with { Connection = connection };
}

public interface IUsageStateStore
{
    ProviderRuntimeState? Get(ProviderId provider);
    bool TryPublish(ProviderRuntimeState candidate);
    bool TryPublishActivity(ProviderId provider, ActivityReading? activity);
    void Disconnect(ProviderId provider);
    void Clear();
}

public sealed class InMemoryUsageStateStore : IUsageStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ProviderId, ProviderRuntimeState> _states = [];
    private readonly Dictionary<ProviderId, ActivityReading> _activity = [];

    public ProviderRuntimeState? Get(ProviderId provider)
    {
        lock (_gate)
        {
            return _states.TryGetValue(provider, out var state)
                ? state with { Activity = _activity.GetValueOrDefault(provider) }
                : null;
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

            // Activity is stored beside the quota state, so a quota publish can never drop a live activity
            // reading and an activity publish can never resurrect a superseded quota reading.
            _states[candidate.Connection.Provider] = candidate with { Activity = null };
            return true;
        }
    }

    public bool TryPublishActivity(ProviderId provider, ActivityReading? activity)
    {
        lock (_gate)
        {
            if (activity is null)
            {
                _activity.Remove(provider);
            }
            else
            {
                _activity[provider] = activity;
            }

            return _states.ContainsKey(provider);
        }
    }

    public void Disconnect(ProviderId provider)
    {
        lock (_gate)
        {
            _states.Remove(provider);
            _activity.Remove(provider);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _states.Clear();
            _activity.Clear();
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
    private readonly IUsageCache? _cache;
    private readonly PollingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Action<ProviderRuntimeState>? _onPublished;
    private readonly object _gate = new();
    private readonly Dictionary<ProviderId, Worker> _workers = [];
    private bool _paused;
    private bool _disposed;

    public PollingCoordinator(
        IEnumerable<IUsageProvider> providers,
        IUsageStateStore store,
        IUiDispatcher? dispatcher = null,
        PollingOptions? options = null,
        IUsageCache? cache = null,
        TimeProvider? timeProvider = null,
        Action<ProviderRuntimeState>? onPublished = null)
    {
        _providers = providers.ToDictionary(provider => provider.Provider);
        _store = store;
        _dispatcher = dispatcher ?? new InlineUiDispatcher();
        _options = options ?? PollingOptions.Default;
        _cache = cache;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onPublished = onPublished;
    }

    public async Task StartAsync(ProviderConnection connection, CancellationToken cancellationToken = default)
    {
        if (!connection.Enabled)
        {
            return;
        }

        if (_cache is not null)
        {
            var cached = await _cache.LoadAsync(connection.Provider, cancellationToken);
            if (cached is not null && cached.Connection.Provider == connection.Provider)
            {
                var restored = FreshnessPolicy.Normalize(cached.ForConnection(connection), _timeProvider.GetUtcNow());
                restored = restored with
                {
                    Origin = StateOrigin.CachedStartup,
                    Status = restored.Status.Freshness == DataFreshness.Expired
                        ? restored.Status
                        : restored.Status with { Freshness = DataFreshness.Stale }
                };
                if (_store.TryPublish(restored))
                {
                    // Surface the cached reading immediately so an offline restart shows the last good
                    // value labelled as cached, instead of an empty cell until the first request fails.
                    await _dispatcher.DispatchAsync(() => _onPublished?.Invoke(restored), cancellationToken);
                }
            }
        }
        Start(connection);
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

            if (!connection.Enabled)
            {
                return;
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

    public void SetIdle(ProviderId provider, bool idle)
    {
        lock (_gate)
        {
            if (_workers.TryGetValue(provider, out var worker))
            {
                worker.SetIdle(idle);
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
            await worker.DisposeAsync().ConfigureAwait(false);
        }

        _store.Disconnect(provider);
        if (_cache is not null)
        {
            await _cache.ClearAsync(provider, CancellationToken.None).ConfigureAwait(false);
        }
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
            await worker.DisposeAsync().ConfigureAwait(false);
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
        private int _transientFailures;
        private int _schemaFailures;
        private bool _idle;

        public Worker(PollingCoordinator owner, ProviderConnection connection)
        {
            _owner = owner;
            _connection = connection;
        }

        public ProviderId Provider => _connection.Provider;

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

        public void SetIdle(bool idle)
        {
            lock (_gate)
            {
                _idle = idle;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
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
                    await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _requested = false;
                    }

                    if (_paused)
                    {
                        continue;
                    }

                    try
                    {
                        await ReadOnceAsync().ConfigureAwait(false);
                        _transientFailures = 0;
                        _schemaFailures = 0;
                        await Task.Delay(Interval, _owner._timeProvider, _stop.Token).ConfigureAwait(false);
                    }
                    catch (ProviderReadException exception)
                    {
                        var now = _owner._timeProvider.GetUtcNow();
                        DateTimeOffset? nextAttempt = exception.IsSchemaFailure
                            ? BackoffPolicy.SchemaNextAttempt(now, ++_schemaFailures)
                            : exception.IsTransient
                                ? BackoffPolicy.NextAttempt(now, ++_transientFailures, exception.ServerDeadline)
                                : null;
                        await PublishFailureAsync(exception, now, nextAttempt).ConfigureAwait(false);
                        if (nextAttempt is { } deadline)
                        {
                            var delay = deadline - _owner._timeProvider.GetUtcNow();
                            if (delay > TimeSpan.Zero)
                            {
                                await Task.Delay(delay, _owner._timeProvider, _stop.Token).ConfigureAwait(false);
                            }
                        }
                        if (nextAttempt is null)
                        {
                            continue;
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // An adapter that throws something unmapped must not silently kill this provider's
                        // worker. Treat it as a transient failure, publish it safely, and back off.
                        var now = _owner._timeProvider.GetUtcNow();
                        var nextAttempt = BackoffPolicy.NextAttempt(now, ++_transientFailures, null);
                        await PublishFailureAsync(new ProviderReadException("Provider request failed", true), now, nextAttempt).ConfigureAwait(false);
                        var delay = nextAttempt - _owner._timeProvider.GetUtcNow();
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay, _owner._timeProvider, _stop.Token).ConfigureAwait(false);
                        }
                    }
                    Request(RefreshReason.Timer);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private TimeSpan Interval
        {
            get
            {
                lock (_gate)
                {
                    return _idle ? _owner._options.IdleInterval : _owner._options.ActiveInterval;
                }
            }
        }

        private async Task ReadOnceAsync()
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            operation.CancelAfter(_owner._options.OperationBudget);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            attempt.CancelAfter(_owner._options.AttemptBudget);
            UsageSnapshot? snapshot;
            try
            {
                snapshot = await _owner._providers[_connection.Provider].ReadAsync(_connection, attempt.Token).ConfigureAwait(false);
            }
            catch (ProviderReadException)
            {
                throw;
            }
            catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
            {
                throw new ProviderReadException("Provider attempt timed out.", true);
            }
            if (snapshot is null)
            {
                return;
            }

            if (!_owner.IsCurrent(this))
            {
                return;
            }

            var current = _owner._store.Get(_connection.Provider);
            var previousAccount = current?.Snapshot?.Account.Partition;
            var accountChanged = previousAccount is not null && previousAccount != snapshot.Account.Partition;
            var candidate = new ProviderRuntimeState(
                _connection,
                snapshot,
                new ProviderStatus(AuthenticationState.Authenticated, DataFreshness.Fresh, _owner._timeProvider.GetUtcNow(), _owner._timeProvider.GetUtcNow(), null, false, null),
                current?.CredentialGeneration ?? 0,
                accountChanged ? (current!.AccountGeneration + 1) : current?.AccountGeneration ?? 0);
            candidate = FreshnessPolicy.Normalize(candidate, _owner._timeProvider.GetUtcNow());
            if (_owner.IsCurrent(this) && _owner._store.TryPublish(candidate))
            {
                if (_owner._cache is not null)
                {
                    await _owner._cache.SaveAsync(candidate, _stop.Token).ConfigureAwait(false);
                }
                var published = _owner._store.Get(_connection.Provider) ?? candidate;
                await _owner._dispatcher.DispatchAsync(() => _owner._onPublished?.Invoke(published), _stop.Token).ConfigureAwait(false);
            }
        }

        private async Task PublishFailureAsync(ProviderReadException exception, DateTimeOffset now, DateTimeOffset? nextAttempt)
        {
            if (!_owner.IsCurrent(this))
            {
                return;
            }

            var current = _owner._store.Get(_connection.Provider);
            var lastSuccess = current?.Status.LastSuccess;
            var status = new ProviderStatus(
                exception.AuthenticationHint ?? current?.Status.Authentication ?? AuthenticationState.Discovering,
                lastSuccess is { } success ? FreshnessPolicy.Evaluate(success, now) : DataFreshness.Unknown,
                now,
                lastSuccess,
                nextAttempt,
                false,
                new ErrorState(exception.Category, exception.IsTransient && !exception.IsSchemaFailure, exception.SafeMessage, exception.Code, nextAttempt));
            var candidate = new ProviderRuntimeState(
                _connection,
                current?.Snapshot,
                status,
                current?.CredentialGeneration ?? 0,
                current?.AccountGeneration ?? 0)
            {
                // A failed attempt does not turn a restored reading into a live one. Keep the origin of the
                // reading that is still being shown until a successful read replaces it.
                Origin = current?.Origin ?? StateOrigin.Live,
            };
            if (_owner._store.TryPublish(candidate))
            {
                if (_owner._cache is not null)
                {
                    await _owner._cache.SaveAsync(candidate, _stop.Token).ConfigureAwait(false);
                }
                var published = _owner._store.Get(_connection.Provider) ?? candidate;
                await _owner._dispatcher.DispatchAsync(() => _owner._onPublished?.Invoke(published), _stop.Token).ConfigureAwait(false);
            }
        }
    }

    private bool IsCurrent(Worker worker)
    {
        lock (_gate)
        {
            return !_disposed && _workers.TryGetValue(worker.Provider, out var current) && ReferenceEquals(current, worker);
        }
    }
}
