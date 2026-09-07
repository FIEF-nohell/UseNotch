using Microsoft.Data.Sqlite;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.Providers.OpenAI;

/// <summary>
/// Conservative Codex activity. Codex exposes no status field, so recent writes are reported as
/// estimated recent activity and nothing else. Silence is never turned into idle or waiting.
/// </summary>
public sealed class CodexActivityMonitor(
    CodexSourceResolver? sources = null,
    TimeProvider? clock = null,
    ActivityOptions? options = null) : IActivityMonitor
{
    internal const string StateDatabaseName = "state_5.sqlite";
    private const int MaximumThreads = 8;
    private const int BusyTimeoutMilliseconds = 250;
    private static readonly string[] RequiredThreadColumns = ["id", "rollout_path", "updated_at"];

    private readonly CodexSourceResolver _sources = sources ?? new CodexSourceResolver(inheritedCodexHome: Environment.GetEnvironmentVariable("CODEX_HOME"));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ActivityOptions _options = options ?? ActivityOptions.Default;

    public ProviderId Provider => ProviderId.OpenAi;

    public Task<ActivityReading> ObserveAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var root = _sources.Resolve().RootPath;
        var database = Path.Combine(root, StateDatabaseName);
        if (!File.Exists(database))
        {
            return Task.FromResult(ActivityReading.Unsupported(Provider, now, "Codex activity records are unavailable"));
        }

        try
        {
            using var connection = OpenReadOnly(database);
            if (!HasSupportedSchema(connection))
            {
                return Task.FromResult(ActivityReading.Unsupported(Provider, now, "Codex activity record format is unsupported"));
            }

            var latest = ReadLatestObservation(connection, root, now, cancellationToken);
            if (latest is not { } observedAt || now - observedAt > _options.EstimatedExpiry)
            {
                // No credible recent write. Codex cannot tell idle apart from unknown, so report neither.
                return Task.FromResult(ActivityReading.Supported(Provider, now, null, 0));
            }

            var session = new ActivitySession(
                "codex:recent",
                Provider,
                ActivityState.Working,
                observedAt,
                observedAt,
                ReadingFidelity.Derived,
                null);
            return Task.FromResult(ActivityReading.Supported(Provider, now, session, 1));
        }
        catch (SqliteException)
        {
            return Task.FromResult(ActivityReading.Unsupported(Provider, now, "Codex activity records could not be read"));
        }
        catch (IOException)
        {
            return Task.FromResult(ActivityReading.Unsupported(Provider, now, "Codex activity records could not be read"));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(ActivityReading.Unsupported(Provider, now, "Codex activity records could not be read"));
        }
    }

    private static SqliteConnection OpenReadOnly(string database)
    {
        // Read-only, private cache, and a short busy timeout. The owning tool keeps this database in WAL
        // mode while it runs, so no immutable fallback is used and its writes stay visible.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = " + BusyTimeoutMilliseconds + ";";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static bool HasSupportedSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('threads');";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return RequiredThreadColumns.All(columns.Contains);
    }

    private static DateTimeOffset? ReadLatestObservation(
        SqliteConnection connection,
        string approvedRoot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rollout_path, updated_at FROM threads ORDER BY updated_at DESC LIMIT " + MaximumThreads + ";";
        DateTimeOffset? latest = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.IsDBNull(1) && TryFromUnixSeconds(reader.GetInt64(1), now) is { } updated)
            {
                latest = Later(latest, updated);
            }

            var rolloutPath = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (TryReadApprovedRolloutWrite(rolloutPath, approvedRoot, now) is { } written)
            {
                latest = Later(latest, written);
            }
        }

        return latest;
    }

    /// <summary>
    /// Follows a record-supplied path only when it resolves inside the approved Codex root, and reads
    /// nothing but its metadata. Rollout content is never opened to infer activity.
    /// </summary>
    private static DateTimeOffset? TryReadApprovedRolloutWrite(string? rolloutPath, string approvedRoot, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(rolloutPath))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(rolloutPath);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedRoot));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var info = new FileInfo(full);
            return info.Exists ? Clamp(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), now) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryFromUnixSeconds(long value, DateTimeOffset now)
    {
        try
        {
            return Clamp(DateTimeOffset.FromUnixTimeSeconds(value), now);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // A clock skew in a foreign record must not create activity that is permanently "just now".
    private static DateTimeOffset Clamp(DateTimeOffset value, DateTimeOffset now) => value > now ? now : value;

    private static DateTimeOffset Later(DateTimeOffset? left, DateTimeOffset right) => left is { } value && value > right ? value : right;
}
