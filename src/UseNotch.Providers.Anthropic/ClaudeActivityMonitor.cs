using System.Globalization;
using System.Text.Json;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.Providers.Anthropic;

/// <summary>
/// One Claude Code session record as this adapter is willing to interpret it. Working directories,
/// session names, and socket paths are deliberately not carried past the parser.
/// </summary>
public sealed record ClaudeSessionRecord(
    string SessionId,
    int ProcessId,
    long? ProcessCreationFileTime,
    string? Status,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

public static class ClaudeSessionRecordParser
{
    public const long MaximumRecordBytes = 64 * 1024;

    public static ClaudeSessionRecord? TryParse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("sessionId", out var sessionId) || sessionId.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(sessionId.GetString())
                || !root.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number || !pid.TryGetInt32(out var processId)
                || !root.TryGetProperty("updatedAt", out var updated) || updated.ValueKind != JsonValueKind.Number || !updated.TryGetInt64(out var updatedMilliseconds))
            {
                return null;
            }

            var started = root.TryGetProperty("startedAt", out var startedAt) && startedAt.ValueKind == JsonValueKind.Number && startedAt.TryGetInt64(out var startedMilliseconds)
                ? FromUnixMilliseconds(startedMilliseconds)
                : null;
            var status = root.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : null;
            var updatedTimestamp = FromUnixMilliseconds(updatedMilliseconds);
            if (updatedTimestamp is null)
            {
                return null;
            }

            return new ClaudeSessionRecord(
                sessionId.GetString()!,
                processId,
                ReadProcessCreationFileTime(root),
                status,
                started ?? updatedTimestamp.Value,
                updatedTimestamp.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The observed records carry the Windows creation time as a FILETIME string. A value that cannot be
    /// read stays null, which the process inspector treats as uncertainty rather than a match.
    /// </summary>
    private static long? ReadProcessCreationFileTime(JsonElement root)
    {
        if (!root.TryGetProperty("procStart", out var procStart))
        {
            return null;
        }

        return procStart.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(procStart.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 => parsed,
            JsonValueKind.Number when procStart.TryGetInt64(out var value) && value > 0 => value,
            _ => null,
        };
    }

    private static DateTimeOffset? FromUnixMilliseconds(long value)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

public static class ClaudeActivityStateMap
{
    /// <summary>
    /// Maps only the status values this adapter recognizes. Anything else is unknown activity, never a
    /// silently assumed idle state.
    /// </summary>
    public static ActivityState Map(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "busy" or "working" or "running" or "active" or "thinking" => ActivityState.Working,
        "waiting" or "blocked" or "needs_input" or "waiting_for_input" or "awaiting_input" => ActivityState.Waiting,
        "idle" or "ready" => ActivityState.Idle,
        _ => ActivityState.Unknown,
    };
}

public sealed class ClaudeActivityMonitor(
    ClaudeSourceResolver? sources = null,
    IProcessInspector? processes = null,
    TimeProvider? clock = null,
    ActivityOptions? options = null) : IActivityMonitor, IActivityChangeSource
{
    private const int MaximumRecords = 64;
    private readonly ClaudeSourceResolver _sources = sources ?? new ClaudeSourceResolver(inheritedClaudeConfigDir: Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));
    private readonly IProcessInspector _processes = processes ?? new SystemProcessInspector();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ActivityOptions _options = options ?? ActivityOptions.Default;

    public ProviderId Provider => ProviderId.Anthropic;

    /// <summary>
    /// Watches the session directory so a status change is picked up within the debounce window instead
    /// of at the next reconciliation pass. A watcher that cannot start, or that overflows, simply leaves
    /// the caller on its reconciliation cadence.
    /// </summary>
    public IDisposable Subscribe(Action onChanged)
    {
        try
        {
            var directory = Path.Combine(_sources.Resolve().RootPath, "sessions");
            // Never create directories inside another tool's data. Without the directory there is nothing
            // to watch, and the reconciliation pass will notice once it appears.
            return Directory.Exists(directory)
                ? new SessionDirectoryWatcher(directory, _options.Debounce, onChanged)
                : new NullSubscription();
        }
        catch (ArgumentException)
        {
            return new NullSubscription();
        }
        catch (IOException)
        {
            return new NullSubscription();
        }
        catch (UnauthorizedAccessException)
        {
            return new NullSubscription();
        }
    }

    public async Task<ActivityReading> ObserveAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var directory = Path.Combine(_sources.Resolve().RootPath, "sessions");
        if (!Directory.Exists(directory))
        {
            return ActivityReading.Unsupported(Provider, now, "Claude Code session records are unavailable");
        }

        var sessions = new List<ActivitySession>();
        var inspected = 0;
        var recognized = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inspected > MaximumRecords)
            {
                break;
            }

            var record = await TryReadAsync(file, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                continue;
            }

            recognized++;
            var liveness = _processes.Inspect(record.ProcessId, record.ProcessCreationFileTime);
            if (liveness == ProcessLiveness.Gone)
            {
                continue;
            }

            // A confirmed live process makes the recorded status a first-hand reading. Access-denied or
            // unverifiable process inspection downgrades it to an estimate instead of dropping it.
            var fidelity = liveness == ProcessLiveness.Alive ? ReadingFidelity.ProviderReported : ReadingFidelity.Derived;
            var state = ClaudeActivityStateMap.Map(record.Status);
            var lastObserved = record.UpdatedAt > now ? now : record.UpdatedAt;
            sessions.Add(new ActivitySession(
                "claude:" + ClaudeCredentialReader.Fingerprint(record.SessionId),
                Provider,
                state,
                record.StartedAt > lastObserved ? lastObserved : record.StartedAt,
                lastObserved,
                fidelity,
                null));
        }

        if (inspected > 0 && recognized == 0)
        {
            return ActivityReading.Unsupported(Provider, now, "Claude Code session record format is unsupported");
        }

        return ActivityReading.Supported(Provider, now, ActivityAggregator.Summarize(sessions, now, _options.EstimatedExpiry), sessions.Count);
    }

    private static async Task<ClaudeSessionRecord?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > ClaudeSessionRecordParser.MaximumRecordBytes)
            {
                return null;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            return ClaudeSessionRecordParser.TryParse(memory.ToArray());
        }
        catch (IOException)
        {
            // A record being replaced while it is read is normal. Skip it and observe it next time.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed class NullSubscription : IDisposable
{
    public void Dispose()
    {
    }
}

internal sealed class SessionDirectoryWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Action _onChanged;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;

    public SessionDirectoryWatcher(string directory, TimeSpan debounce, Action onChanged)
    {
        _onChanged = onChanged;
        _debounce = debounce <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(150) : debounce;
        _watcher = new FileSystemWatcher(directory, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        _watcher.Changed += OnEvent;
        _watcher.Created += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Renamed += OnEvent;
        // An overflowed buffer means events were lost, so ask for a full pass rather than nothing.
        _watcher.Error += (_, _) => Schedule();
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Schedule();

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Coalesce a burst of writes into one observation.
            _timer ??= new Timer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        _onChanged();
    }
}
