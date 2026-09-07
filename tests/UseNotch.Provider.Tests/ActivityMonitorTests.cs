using System.Text;
using Microsoft.Data.Sqlite;
using UseNotch.Application;
using UseNotch.Domain;
using UseNotch.Providers.Anthropic;
using UseNotch.Providers.OpenAI;

namespace UseNotch.Provider.Tests;

internal sealed class StubProcessInspector(ProcessLiveness liveness) : IProcessInspector
{
    public List<(int ProcessId, long? Creation)> Inspections { get; } = [];

    public ProcessLiveness Inspect(int processId, long? expectedCreationFileTime)
    {
        Inspections.Add((processId, expectedCreationFileTime));
        return liveness;
    }
}

public sealed class ClaudeActivityMonitorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UseNotch.Tests", Guid.NewGuid().ToString("N"));

    // Mirrors a record written by Claude Code 2.1.263 on Windows, observed on 2026-09-07. The values are
    // synthetic and the fields this adapter refuses to read are present on purpose.
    private static string Record(string status, long updatedAt, int pid = 4242, string procStart = "134332580528978418", string sessionId = "11111111-2222-3333-4444-555555555555")
        => $$"""
            {"pid":{{pid}},"sessionId":"{{sessionId}}","cwd":"C:\\Users\\someone\\private-project","startedAt":{{updatedAt - 1000}},
             "procStart":"{{procStart}}","version":"2.1.263","kind":"interactive","entrypoint":"cli","pidDomain":"win32:host",
             "messagingSocketPath":"\\\\.\\pipe\\LOCAL\\cc-msg-private","name":"private-name","status":"{{status}}",
             "updatedAt":{{updatedAt}},"statusUpdatedAt":{{updatedAt}}}
            """;

    private string SessionsDirectory
    {
        get
        {
            var directory = Path.Combine(_root, "sessions");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private ClaudeActivityMonitor CreateMonitor(IProcessInspector inspector)
        => new(new ClaudeSourceResolver(_root), inspector);

    [Fact]
    public async Task A_record_change_signals_the_subscriber_within_the_debounce_window()
    {
        var directory = SessionsDirectory;
        var monitor = new ClaudeActivityMonitor(
            new ClaudeSourceResolver(_root),
            new StubProcessInspector(ProcessLiveness.Alive),
            options: ActivityOptions.Default with { Debounce = TimeSpan.FromMilliseconds(50) });
        var signalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = monitor.Subscribe(() => signalled.TrySetResult(true));
        await File.WriteAllTextAsync(Path.Combine(directory, "4242.json"), Record("busy", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        Assert.True(await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Subscribing_without_a_sessions_directory_creates_nothing_and_still_returns_a_subscription()
    {
        Directory.CreateDirectory(_root);
        var monitor = new ClaudeActivityMonitor(new ClaudeSourceResolver(_root), new StubProcessInspector(ProcessLiveness.Alive));

        using var subscription = monitor.Subscribe(() => { });

        Assert.NotNull(subscription);
        Assert.False(Directory.Exists(Path.Combine(_root, "sessions")));
    }

    [Fact]
    public async Task A_missing_sessions_directory_is_unsupported_rather_than_idle()
    {
        Directory.CreateDirectory(_root);

        var reading = await CreateMonitor(new StubProcessInspector(ProcessLiveness.Alive)).ObserveAsync(CancellationToken.None);

        Assert.Equal(ActivityCapability.Unsupported, reading.Capability);
        Assert.Null(reading.Session);
    }

    [Theory]
    [InlineData("busy", ActivityState.Working)]
    [InlineData("waiting", ActivityState.Waiting)]
    [InlineData("idle", ActivityState.Idle)]
    [InlineData("something-new", ActivityState.Unknown)]
    public async Task Recognized_statuses_map_and_unknown_ones_stay_unknown(string status, ActivityState expected)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await File.WriteAllTextAsync(Path.Combine(SessionsDirectory, "4242.json"), Record(status, now));

        var reading = await CreateMonitor(new StubProcessInspector(ProcessLiveness.Alive)).ObserveAsync(CancellationToken.None);

        Assert.Equal(ActivityCapability.Supported, reading.Capability);
        Assert.Equal(expected, reading.Session!.State);
        Assert.Equal(ReadingFidelity.ProviderReported, reading.Session.Fidelity);
    }

    [Fact]
    public async Task A_session_never_carries_a_working_directory_a_name_or_its_raw_identifier()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await File.WriteAllTextAsync(Path.Combine(SessionsDirectory, "4242.json"), Record("busy", now));

        var reading = await CreateMonitor(new StubProcessInspector(ProcessLiveness.Alive)).ObserveAsync(CancellationToken.None);

        var session = reading.Session!;
        Assert.Null(session.SafeLabel);
        Assert.DoesNotContain("private", session.Id, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("11111111", session.Id, StringComparison.Ordinal);
        Assert.StartsWith("claude:", session.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_whose_process_is_gone_is_dropped()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await File.WriteAllTextAsync(Path.Combine(SessionsDirectory, "4242.json"), Record("busy", now));

        var reading = await CreateMonitor(new StubProcessInspector(ProcessLiveness.Gone)).ObserveAsync(CancellationToken.None);

        Assert.Equal(ActivityCapability.Supported, reading.Capability);
        Assert.Equal(0, reading.ObservedSessions);
        Assert.Null(reading.Session);
    }

    [Fact]
    public async Task Access_denied_process_inspection_downgrades_the_reading_to_an_estimate()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await File.WriteAllTextAsync(Path.Combine(SessionsDirectory, "4242.json"), Record("busy", now));

        var reading = await CreateMonitor(new StubProcessInspector(ProcessLiveness.Uncertain)).ObserveAsync(CancellationToken.None);

        Assert.Equal(ReadingFidelity.Derived, reading.Session!.Fidelity);
        Assert.Equal(ActivityState.Working, reading.Session.State);
    }

    [Fact]
    public async Task The_recorded_process_creation_time_is_passed_to_the_inspector_to_defend_against_reuse()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await File.WriteAllTextAsync(Path.Combine(SessionsDirectory, "4242.json"), Record("busy", now, pid: 4242, procStart: "134332580528978418"));
        var inspector = new StubProcessInspector(ProcessLiveness.Alive);

        await CreateMonitor(inspector).ObserveAsync(CancellationToken.None);

        var inspection = Assert.Single(inspector.Inspections);
        Assert.Equal(4242, inspection.ProcessId);
        Assert.Equal(134332580528978418L, inspection.Creation);
    }

    [Fact]
    public async Task A_record_without_a_creation_time_reports_no_expected_value_rather_than_a_guess()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await File.WriteAllTextAsync(
            Path.Combine(SessionsDirectory, "4242.json"),
            $"{{\"pid\":4242,\"sessionId\":\"abc\",\"status\":\"busy\",\"updatedAt\":{now}}}");
        var inspector = new StubProcessInspector(ProcessLiveness.Uncertain);

        await CreateMonitor(inspector).ObserveAsync(CancellationToken.None);

        Assert.Null(Assert.Single(inspector.Inspections).Creation);
    }

    [Fact]
    public async Task A_partial_write_is_skipped_while_the_remaining_records_still_report()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var directory = SessionsDirectory;
        await File.WriteAllTextAsync(Path.Combine(directory, "1.json"), "{\"pid\":1,\"sessionId\":\"a\",\"status\":\"bu");
        await File.WriteAllTextAsync(Path.Combine(directory, "2.json"), Record("busy", now, pid: 2, sessionId: "22222222-2222-3333-4444-555555555555"));

        var reading = await CreateMonitor(new StubProcessInspector(ProcessLiveness.Alive)).ObserveAsync(CancellationToken.None);

        Assert.Equal(1, reading.ObservedSessions);
        Assert.Equal(ActivityState.Working, reading.Session!.State);
    }

    [Fact]
    public async Task Records_that_are_all_unrecognized_report_an_unsupported_format()
    {
        await File.WriteAllTextAsync(Path.Combine(SessionsDirectory, "1.json"), "{\"somethingElse\":true}");

        var reading = await CreateMonitor(new StubProcessInspector(ProcessLiveness.Alive)).ObserveAsync(CancellationToken.None);

        Assert.Equal(ActivityCapability.Unsupported, reading.Capability);
        Assert.Equal("Claude Code session record format is unsupported", reading.UnsupportedReason);
    }

    [Fact]
    public async Task An_oversized_record_is_skipped_instead_of_being_read_into_memory()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var padded = Record("busy", now).TrimEnd().TrimEnd('}') + ",\"padding\":\"" + new string('x', 80 * 1024) + "\"}";
        await File.WriteAllTextAsync(Path.Combine(SessionsDirectory, "4242.json"), padded);

        var reading = await CreateMonitor(new StubProcessInspector(ProcessLiveness.Alive)).ObserveAsync(CancellationToken.None);

        Assert.Equal(ActivityCapability.Unsupported, reading.Capability);
    }

    [Fact]
    public async Task A_future_timestamp_is_clamped_so_activity_cannot_be_permanently_current()
    {
        var future = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds();
        await File.WriteAllTextAsync(Path.Combine(SessionsDirectory, "4242.json"), Record("busy", future));

        var reading = await CreateMonitor(new StubProcessInspector(ProcessLiveness.Alive)).ObserveAsync(CancellationToken.None);

        Assert.True(reading.Session!.LastObservedAt <= reading.ObservedAt);
        Assert.True(reading.Session.StartedAt <= reading.Session.LastObservedAt);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}

public sealed class CodexActivityMonitorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UseNotch.Tests", Guid.NewGuid().ToString("N"));

    private CodexActivityMonitor CreateMonitor(TimeProvider? clock = null)
        => new(new CodexSourceResolver(_root), clock);

    private string CreateDatabase(string schema, IEnumerable<(string? RolloutPath, long? UpdatedAt)> rows)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "state_5.sqlite");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = schema;
            create.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO threads (id, rollout_path, updated_at) VALUES ($id, $path, $updated);";
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            insert.Parameters.AddWithValue("$path", (object?)row.RolloutPath ?? DBNull.Value);
            insert.Parameters.AddWithValue("$updated", (object?)row.UpdatedAt ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private const string SupportedSchema = "CREATE TABLE threads (id TEXT PRIMARY KEY, rollout_path TEXT, updated_at INTEGER, cwd TEXT, title TEXT);";

    [Fact]
    public async Task A_missing_state_database_is_unsupported()
    {
        Directory.CreateDirectory(_root);

        var reading = await CreateMonitor().ObserveAsync(CancellationToken.None);

        Assert.Equal(ActivityCapability.Unsupported, reading.Capability);
        Assert.Null(reading.Session);
    }

    [Fact]
    public async Task An_unknown_schema_is_unsupported_rather_than_queried()
    {
        CreateDatabase("CREATE TABLE threads (id TEXT PRIMARY KEY, something_else TEXT);", []);

        var reading = await CreateMonitor().ObserveAsync(CancellationToken.None);

        Assert.Equal(ActivityCapability.Unsupported, reading.Capability);
        Assert.Equal("Codex activity record format is unsupported", reading.UnsupportedReason);
    }

    [Fact]
    public async Task A_recent_write_is_reported_as_estimated_working_activity()
    {
        var now = DateTimeOffset.UtcNow;
        CreateDatabase(SupportedSchema, [(null, now.AddSeconds(-2).ToUnixTimeSeconds())]);

        var reading = await CreateMonitor().ObserveAsync(CancellationToken.None);

        Assert.Equal(ActivityCapability.Supported, reading.Capability);
        Assert.Equal(ActivityState.Working, reading.Session!.State);
        Assert.Equal(ReadingFidelity.Derived, reading.Session.Fidelity);
    }

    [Fact]
    public async Task An_old_write_reports_no_session_rather_than_an_idle_or_waiting_one()
    {
        var now = DateTimeOffset.UtcNow;
        CreateDatabase(SupportedSchema, [(null, now.AddMinutes(-30).ToUnixTimeSeconds())]);

        var reading = await CreateMonitor().ObserveAsync(CancellationToken.None);

        Assert.Equal(ActivityCapability.Supported, reading.Capability);
        Assert.Null(reading.Session);
    }

    [Fact]
    public async Task A_rollout_path_outside_the_approved_root_is_not_followed()
    {
        var outsideRoot = Path.Combine(Path.GetTempPath(), "UseNotch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        var outsideFile = Path.Combine(outsideRoot, "rollout.jsonl");
        await File.WriteAllTextAsync(outsideFile, "{}");
        try
        {
            var now = DateTimeOffset.UtcNow;
            CreateDatabase(SupportedSchema, [(outsideFile, now.AddMinutes(-30).ToUnixTimeSeconds())]);

            var reading = await CreateMonitor().ObserveAsync(CancellationToken.None);

            // The freshly written file would look like current activity if the path were followed.
            Assert.Null(reading.Session);
        }
        finally
        {
            Directory.Delete(outsideRoot, true);
        }
    }

    [Fact]
    public async Task A_rollout_path_inside_the_approved_root_contributes_its_write_time()
    {
        var sessions = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessions);
        var inside = Path.Combine(sessions, "rollout.jsonl");
        var now = DateTimeOffset.UtcNow;
        CreateDatabase(SupportedSchema, [(inside, now.AddMinutes(-30).ToUnixTimeSeconds())]);
        await File.WriteAllTextAsync(inside, "{}");

        var reading = await CreateMonitor().ObserveAsync(CancellationToken.None);

        Assert.Equal(ActivityState.Working, reading.Session!.State);
    }

    [Fact]
    public async Task A_future_timestamp_is_clamped_instead_of_creating_permanent_activity()
    {
        var now = DateTimeOffset.UtcNow;
        CreateDatabase(SupportedSchema, [(null, now.AddDays(1).ToUnixTimeSeconds())]);

        var reading = await CreateMonitor().ObserveAsync(CancellationToken.None);

        Assert.NotNull(reading.Session);
        Assert.True(reading.Session!.LastObservedAt <= reading.ObservedAt);
    }

    [Fact]
    public async Task A_live_write_ahead_log_change_is_observed_without_an_immutable_fallback()
    {
        var now = DateTimeOffset.UtcNow;
        var path = CreateDatabase(SupportedSchema, [(null, now.AddMinutes(-30).ToUnixTimeSeconds())]);
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        writer.Open();
        using (var walMode = writer.CreateCommand())
        {
            walMode.CommandText = "PRAGMA journal_mode = WAL;";
            walMode.ExecuteScalar();
        }

        Assert.Null((await CreateMonitor().ObserveAsync(CancellationToken.None)).Session);

        using (var update = writer.CreateCommand())
        {
            update.CommandText = "UPDATE threads SET updated_at = $updated;";
            update.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            update.ExecuteNonQuery();
        }

        var reading = await CreateMonitor().ObserveAsync(CancellationToken.None);

        Assert.Equal(ActivityState.Working, reading.Session!.State);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}

public sealed class ClaudeSessionRecordParserTests
{
    [Fact]
    public void A_record_missing_its_required_fields_is_not_recognized()
    {
        Assert.Null(ClaudeSessionRecordParser.TryParse(Encoding.UTF8.GetBytes("{\"sessionId\":\"a\",\"status\":\"busy\"}")));
        Assert.Null(ClaudeSessionRecordParser.TryParse(Encoding.UTF8.GetBytes("{\"pid\":1,\"updatedAt\":1}")));
        Assert.Null(ClaudeSessionRecordParser.TryParse(Encoding.UTF8.GetBytes("not json")));
    }

    [Fact]
    public void A_creation_time_is_read_from_a_string_or_a_number_and_never_guessed()
    {
        var fromString = ClaudeSessionRecordParser.TryParse(Encoding.UTF8.GetBytes("{\"pid\":1,\"sessionId\":\"a\",\"updatedAt\":1,\"procStart\":\"134332580528978418\"}"));
        var fromNumber = ClaudeSessionRecordParser.TryParse(Encoding.UTF8.GetBytes("{\"pid\":1,\"sessionId\":\"a\",\"updatedAt\":1,\"procStart\":134332580528978418}"));
        var unusable = ClaudeSessionRecordParser.TryParse(Encoding.UTF8.GetBytes("{\"pid\":1,\"sessionId\":\"a\",\"updatedAt\":1,\"procStart\":\"not-a-number\"}"));

        Assert.Equal(134332580528978418L, fromString!.ProcessCreationFileTime);
        Assert.Equal(134332580528978418L, fromNumber!.ProcessCreationFileTime);
        Assert.Null(unusable!.ProcessCreationFileTime);
    }
}
