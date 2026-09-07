using System.Diagnostics;
using UseNotch.Domain;

namespace UseNotch.Application;

public enum ActivityCapability { Unknown, Supported, Unsupported }

public enum ProcessLiveness { Alive, Gone, Uncertain }

/// <summary>
/// One provider's current activity picture. Capability, the summarized session, and the number of
/// observed sessions are kept apart so an unsupported source stays an honest unavailable state rather
/// than looking like an idle one.
/// </summary>
public sealed record ActivityReading(
    ProviderId Provider,
    ActivityCapability Capability,
    ActivitySession? Session,
    int ObservedSessions,
    DateTimeOffset ObservedAt,
    string? UnsupportedReason)
{
    public static ActivityReading Unsupported(ProviderId provider, DateTimeOffset observedAt, string reason)
        => new(provider, ActivityCapability.Unsupported, null, 0, observedAt, reason);

    public static ActivityReading Supported(ProviderId provider, DateTimeOffset observedAt, ActivitySession? session, int observedSessions)
        => new(provider, ActivityCapability.Supported, session, observedSessions, observedAt, null);
}

public interface IActivityMonitor
{
    ProviderId Provider { get; }

    Task<ActivityReading> ObserveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// An activity source that can say when something changed, so a supported source updates promptly
/// instead of waiting for the next reconciliation pass.
/// </summary>
public interface IActivityChangeSource
{
    IDisposable Subscribe(Action onChanged);
}

public interface IProcessInspector
{
    /// <summary>
    /// Reports whether a recorded process is still the same process. A creation time that cannot be read
    /// is uncertainty, never a confirmed match, so process-id reuse cannot be mistaken for liveness.
    /// </summary>
    ProcessLiveness Inspect(int processId, long? expectedCreationFileTime);
}

public sealed class SystemProcessInspector : IProcessInspector
{
    private static readonly TimeSpan CreationTolerance = TimeSpan.FromSeconds(2);

    public ProcessLiveness Inspect(int processId, long? expectedCreationFileTime)
    {
        if (processId <= 0)
        {
            return ProcessLiveness.Gone;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return ProcessLiveness.Gone;
            }

            if (expectedCreationFileTime is not { } expected)
            {
                return ProcessLiveness.Uncertain;
            }

            var actual = process.StartTime.ToUniversalTime().ToFileTimeUtc();
            return Math.Abs(actual - expected) <= CreationTolerance.Ticks ? ProcessLiveness.Alive : ProcessLiveness.Gone;
        }
        catch (ArgumentException)
        {
            return ProcessLiveness.Gone;
        }
        catch (InvalidOperationException)
        {
            return ProcessLiveness.Gone;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ProcessLiveness.Uncertain;
        }
        catch (NotSupportedException)
        {
            return ProcessLiveness.Uncertain;
        }
    }
}

public sealed record ActivityOptions(
    TimeSpan Interval,
    TimeSpan IdleInterval,
    TimeSpan EstimatedExpiry,
    TimeSpan Debounce,
    TimeSpan AttemptBudget)
{
    public static ActivityOptions Default { get; } = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromSeconds(5));
}

public static class ActivityAggregator
{
    /// <summary>
    /// Folds observed sessions into the single session a provider cell can show. Estimated observations
    /// expire, and waiting only wins when a source explicitly reported it.
    /// </summary>
    public static ActivitySession? Summarize(
        IReadOnlyList<ActivitySession> sessions,
        DateTimeOffset now,
        TimeSpan estimatedExpiry)
    {
        ActivitySession? best = null;
        var bestRank = int.MinValue;
        foreach (var session in sessions)
        {
            if (session.Fidelity == ReadingFidelity.Derived && now - session.LastObservedAt > estimatedExpiry)
            {
                continue;
            }

            var rank = Rank(session);
            if (rank > bestRank || (rank == bestRank && best is not null && session.LastObservedAt > best.LastObservedAt))
            {
                best = session;
                bestRank = rank;
            }
        }

        return best;
    }

    private static int Rank(ActivitySession session) => session.State switch
    {
        // Waiting is the most actionable state, but only a source that actually reported it may claim it.
        ActivityState.Waiting when session.Fidelity == ReadingFidelity.ProviderReported => 4,
        ActivityState.Waiting => 2,
        ActivityState.Working => 3,
        ActivityState.Idle => 1,
        _ => 0,
    };
}

/// <summary>
/// Runs one independent worker per activity monitor. Activity never shares a worker with quota, so an
/// unsupported or failing activity source cannot delay or break a quota reading.
/// </summary>
public sealed class ActivityCoordinator : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<ProviderId, IActivityMonitor> _monitors;
    private readonly IUsageStateStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly ActivityOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Action<ProviderId>? _onPublished;
    private readonly object _gate = new();
    private readonly Dictionary<ProviderId, Worker> _workers = [];
    private bool _paused;
    private bool _disposed;

    public ActivityCoordinator(
        IEnumerable<IActivityMonitor> monitors,
        IUsageStateStore store,
        IUiDispatcher? dispatcher = null,
        ActivityOptions? options = null,
        TimeProvider? timeProvider = null,
        Action<ProviderId>? onPublished = null)
    {
        _monitors = monitors.ToDictionary(monitor => monitor.Provider);
        _store = store;
        _dispatcher = dispatcher ?? new InlineUiDispatcher();
        _options = options ?? ActivityOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onPublished = onPublished;
    }

    public void Start(ProviderId provider)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_monitors.ContainsKey(provider))
            {
                throw new InvalidOperationException($"No activity monitor registered for {provider}.");
            }

            if (_workers.ContainsKey(provider))
            {
                return;
            }

            var worker = new Worker(this, provider);
            _workers.Add(provider, worker);
            worker.Start();
        }
    }

    public void SetPaused(bool paused)
    {
        Worker[] workers;
        lock (_gate)
        {
            _paused = paused;
            workers = [.. _workers.Values];
        }

        foreach (var worker in workers)
        {
            worker.SetPaused(paused);
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

        _store.TryPublishActivity(provider, null);
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
            workers = [.. _workers.Values];
            _workers.Clear();
        }

        foreach (var worker in workers)
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool IsCurrent(Worker worker)
    {
        lock (_gate)
        {
            return !_disposed && _workers.TryGetValue(worker.Provider, out var current) && ReferenceEquals(current, worker);
        }
    }

    private sealed class Worker(ActivityCoordinator owner, ProviderId provider) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _changed = new(0, 1);
        private IDisposable? _subscription;
        private Task? _loop;
        private bool _paused = owner._paused;

        public ProviderId Provider => provider;

        public void Start()
        {
            if (owner._monitors[provider] is IActivityChangeSource source)
            {
                _subscription = source.Subscribe(Signal);
            }

            _loop = Task.Run(RunAsync);
        }

        public void SetPaused(bool paused)
        {
            Volatile.Write(ref _paused, paused);
            if (!paused)
            {
                Signal();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _subscription?.Dispose();
            _subscription = null;
            _stop.Cancel();
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }

            _changed.Dispose();
            _stop.Dispose();
        }

        private void Signal()
        {
            try
            {
                if (_changed.CurrentCount == 0)
                {
                    _changed.Release();
                }
            }
            catch (SemaphoreFullException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var interval = owner._options.IdleInterval;
                    if (!Volatile.Read(ref _paused))
                    {
                        interval = await ObserveOnceAsync().ConfigureAwait(false);
                    }

                    // Reconcile on a fixed cadence, but react promptly when the source says a record
                    // changed. A source that writes continuously must not be able to drive observation at
                    // the debounce rate, so a minimum spacing is always waited out first.
                    var minimumSpacing = owner._options.Interval < interval ? owner._options.Interval : interval;
                    await Task.Delay(minimumSpacing, owner._timeProvider, _stop.Token).ConfigureAwait(false);
                    if (interval > minimumSpacing)
                    {
                        await WaitAsync(interval - minimumSpacing).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }

        private async Task WaitAsync(TimeSpan interval)
        {
            var delay = Task.Delay(interval, owner._timeProvider, _stop.Token);
            var signalled = _changed.WaitAsync(_stop.Token);
            var completed = await Task.WhenAny(delay, signalled).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }

        private async Task<TimeSpan> ObserveOnceAsync()
        {
            var now = owner._timeProvider.GetUtcNow();
            ActivityReading reading;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            attempt.CancelAfter(owner._options.AttemptBudget);
            try
            {
                reading = await owner._monitors[provider].ObserveAsync(attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // An activity source that misbehaves reports unavailable activity. It never surfaces its
                // own message, which could carry a path or a title, and never affects quota.
                reading = ActivityReading.Unsupported(provider, now, "Activity unavailable");
            }

            if (!owner.IsCurrent(this))
            {
                return owner._options.IdleInterval;
            }

            if (owner._store.TryPublishActivity(provider, reading))
            {
                await owner._dispatcher.DispatchAsync(() => owner._onPublished?.Invoke(provider), _stop.Token).ConfigureAwait(false);
            }

            // A source that signals its own changes only needs the slower reconciliation cadence, because
            // anything interesting arrives through the signal. The fast scan is for a source that has to
            // be sampled to notice anything at all, and only while something is actually happening.
            if (_subscription is not null)
            {
                return owner._options.IdleInterval;
            }

            return reading.Capability == ActivityCapability.Supported && reading.Session is not null
                ? owner._options.Interval
                : owner._options.IdleInterval;
        }
    }
}
