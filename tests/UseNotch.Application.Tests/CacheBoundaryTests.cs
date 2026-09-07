using System.Text.Json;
using UseNotch.Application;
using UseNotch.Domain;
using UseNotch.Infrastructure;
using UseNotch.TestSupport;

namespace UseNotch.Application.Tests;

/// <summary>
/// The MVP persistence boundary: a sanitized last-good JSON cache and nothing else. These tests exist to
/// keep cached data from ever being presented as current, and to keep historical retention out of the
/// first release.
/// </summary>
public sealed class CacheBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UseNotch.Tests", Guid.NewGuid().ToString("N"));

    private static ProviderRuntimeState State(
        string sourceId = "codex:default",
        string account = "account-a",
        DateTimeOffset? retrievedAt = null,
        DateTimeOffset? resetsAt = null,
        long accountGeneration = 0)
    {
        var now = retrievedAt ?? DateTimeOffset.UtcNow;
        var window = new QuotaWindow("session", "5h limit", TimeSpan.FromHours(5), now.AddHours(-1), resetsAt ?? now.AddHours(4), new UsageLimit(1, 4, 5, .2m, "requests"));
        var snapshot = new UsageSnapshot(ProviderId.OpenAi, new AccountScope(account, IdentityConfidence.LocalPartition), now, [window], "session", new SourceDescriptor("Codex auth.json", "codex-file-v1", false), null);
        return new ProviderRuntimeState(
            new ProviderConnection(ProviderId.OpenAi, sourceId, true, 1),
            snapshot,
            new ProviderStatus(AuthenticationState.Authenticated, DataFreshness.Fresh, now, now, null, false, null),
            0,
            accountGeneration);
    }

    private JsonUsageCache CreateCache() => new(_root);

    [Fact]
    public async Task An_offline_restart_serves_the_last_good_reading_marked_as_cached_and_stale()
    {
        var cache = CreateCache();
        await cache.SaveAsync(State(retrievedAt: DateTimeOffset.UtcNow.AddMinutes(-30)), CancellationToken.None);
        var store = new InMemoryUsageStateStore();
        var provider = new BlockingProvider();
        await using var coordinator = new PollingCoordinator([provider], store, cache: cache);

        await coordinator.StartAsync(new ProviderConnection(ProviderId.OpenAi, "codex:default", true, 2));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var restored = store.Get(ProviderId.OpenAi)!;
        Assert.Equal(StateOrigin.CachedStartup, restored.Origin);
        Assert.Equal(DataFreshness.Stale, restored.Status.Freshness);
        Assert.NotNull(restored.Snapshot);
        provider.Release.TrySetResult(true);
    }

    [Fact]
    public async Task A_cached_reading_from_another_source_is_discarded_rather_than_shown()
    {
        var cache = CreateCache();
        await cache.SaveAsync(State(sourceId: "codex:old-root", account: "account-a"), CancellationToken.None);
        var store = new InMemoryUsageStateStore();
        var provider = new BlockingProvider();
        await using var coordinator = new PollingCoordinator([provider], store, cache: cache);

        await coordinator.StartAsync(new ProviderConnection(ProviderId.OpenAi, "codex:new-root", true, 2));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(store.Get(ProviderId.OpenAi));
        Assert.Null(await cache.LoadAsync(ProviderId.OpenAi, CancellationToken.None));
        provider.Release.TrySetResult(true);
    }

    [Fact]
    public async Task A_reading_older_than_a_day_is_expired_and_loses_its_headline()
    {
        var cache = CreateCache();
        await cache.SaveAsync(State(retrievedAt: DateTimeOffset.UtcNow.AddHours(-25)), CancellationToken.None);
        var store = new InMemoryUsageStateStore();
        var provider = new BlockingProvider();
        await using var coordinator = new PollingCoordinator([provider], store, cache: cache);

        await coordinator.StartAsync(new ProviderConnection(ProviderId.OpenAi, "codex:default", true, 2));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var restored = store.Get(ProviderId.OpenAi)!;
        Assert.Equal(DataFreshness.Expired, restored.Status.Freshness);
        Assert.Null(restored.Snapshot!.Headline);
        provider.Release.TrySetResult(true);
    }

    [Fact]
    public async Task A_window_whose_reset_has_passed_loses_its_headline_rather_than_showing_an_old_number()
    {
        var cache = CreateCache();
        await cache.SaveAsync(State(retrievedAt: DateTimeOffset.UtcNow.AddMinutes(-20), resetsAt: DateTimeOffset.UtcNow.AddMinutes(-5)), CancellationToken.None);
        var store = new InMemoryUsageStateStore();
        var provider = new BlockingProvider();
        await using var coordinator = new PollingCoordinator([provider], store, cache: cache);

        await coordinator.StartAsync(new ProviderConnection(ProviderId.OpenAi, "codex:default", true, 2));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(store.Get(ProviderId.OpenAi)!.Snapshot!.Headline);
        provider.Release.TrySetResult(true);
    }

    [Fact]
    public async Task Disconnecting_removes_the_cached_reading_so_it_cannot_return()
    {
        var cache = CreateCache();
        await cache.SaveAsync(State(), CancellationToken.None);
        var store = new InMemoryUsageStateStore();
        var provider = new BlockingProvider();
        await using var coordinator = new PollingCoordinator([provider], store, cache: cache);
        await coordinator.StartAsync(new ProviderConnection(ProviderId.OpenAi, "codex:default", true, 2));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Release.TrySetResult(true);

        await coordinator.DisconnectAsync(ProviderId.OpenAi);

        Assert.Null(store.Get(ProviderId.OpenAi));
        Assert.Null(await cache.LoadAsync(ProviderId.OpenAi, CancellationToken.None));
    }

    [Fact]
    public async Task A_save_that_arrives_after_a_disconnect_cannot_resurrect_the_reading()
    {
        var cache = CreateCache();
        var store = new InMemoryUsageStateStore();
        var provider = new BlockingProvider();
        await using var coordinator = new PollingCoordinator([provider], store, cache: cache);
        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "codex:default", true, 1));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The worker is still inside its read when the disconnect happens. Disconnect awaits the worker,
        // so no save can land afterwards.
        var disconnect = coordinator.DisconnectAsync(ProviderId.OpenAi);
        provider.Release.TrySetResult(true);
        await disconnect;

        Assert.Null(await cache.LoadAsync(ProviderId.OpenAi, CancellationToken.None));
        Assert.Null(store.Get(ProviderId.OpenAi));
    }

    [Fact]
    public async Task The_cache_holds_normalized_quota_only_and_no_activity_or_credential_material()
    {
        var cache = CreateCache();
        var now = DateTimeOffset.UtcNow;
        var state = State() with
        {
            Activity = ActivityReading.Supported(
                ProviderId.OpenAi,
                now,
                new ActivitySession("codex:recent", ProviderId.OpenAi, ActivityState.Working, now, now, ReadingFidelity.Derived, "a private label"),
                1),
        };

        await cache.SaveAsync(state, CancellationToken.None);
        var text = await File.ReadAllTextAsync(Path.Combine(_root, "OpenAi.json"));

        // The property may exist as an explicit null; what must never appear is any activity content.
        foreach (var forbidden in new[] { "a private label", "codex:recent", "Working", "accessToken", "access_token", "refreshToken", "Bearer", "sk-" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }

        using var document = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("State").GetProperty("Activity").ValueKind);
    }

    [Fact]
    public async Task A_cache_written_by_a_future_schema_version_is_ignored_rather_than_guessed_at()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "OpenAi.json"),
            """{"SchemaVersion":99,"SavedAt":"2027-01-15T12:00:00+00:00","State":{"Connection":{"Provider":0,"SourceId":"codex:default","Enabled":true,"Generation":1}}}""");

        Assert.Null(await CreateCache().LoadAsync(ProviderId.OpenAi, CancellationToken.None));
    }

    [Fact]
    public async Task A_corrupt_cache_is_ignored_and_the_provider_still_starts()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "OpenAi.json"), "{ not json at all");
        var store = new InMemoryUsageStateStore();
        var provider = new BlockingProvider();
        await using var coordinator = new PollingCoordinator([provider], store, cache: CreateCache());

        await coordinator.StartAsync(new ProviderConnection(ProviderId.OpenAi, "codex:default", true, 1));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(store.Get(ProviderId.OpenAi));
        Assert.Equal(1, provider.Calls);
        provider.Release.TrySetResult(true);
    }

    [Fact]
    public async Task A_failed_write_leaves_no_partial_file_behind()
    {
        var cache = CreateCache();

        await cache.SaveAsync(State(), CancellationToken.None);
        await cache.SaveAsync(State(account: "account-b"), CancellationToken.None);

        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
        Assert.Single(Directory.GetFiles(_root, "*.json"));
    }

    [Fact]
    public void The_application_owns_no_history_database_and_no_chart_dependency()
    {
        var repositoryRoot = RepositoryFiles.Root;
        var packages = File.ReadAllText(Path.Combine(repositoryRoot, "Directory.Packages.props"));

        foreach (var chartPackage in new[] { "LiveCharts", "OxyPlot", "ScottPlot", "SkiaSharp.Extended" })
        {
            Assert.DoesNotContain(chartPackage, packages, StringComparison.OrdinalIgnoreCase);
        }

        // History is deferred to H01. No production source may create or open a history database.
        var sources = Directory.GetFiles(Path.Combine(repositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        Assert.All(sources, path =>
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("usage.sqlite", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("IHistoryRepository", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Running_a_provider_creates_only_the_sanitized_json_cache()
    {
        var cache = CreateCache();
        var store = new InMemoryUsageStateStore();
        var provider = new SequencedProvider(_ => Task.FromResult<UsageSnapshot?>(State().Snapshot));
        await using var coordinator = new PollingCoordinator([provider], store, cache: cache);

        coordinator.Start(new ProviderConnection(ProviderId.OpenAi, "codex:default", true, 1));
        for (var attempt = 0; attempt < 200 && !File.Exists(Path.Combine(_root, "OpenAi.json")); attempt++)
        {
            await Task.Delay(10);
        }

        var created = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetFileName(path) ?? string.Empty)
            .ToArray();
        Assert.Equal(["OpenAi.json"], created);
    }

    private sealed class BlockingProvider : IUsageProvider
    {
        public ProviderId Provider => ProviderId.OpenAi;
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public async Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return null;
        }
    }

    private sealed class SequencedProvider(Func<ProviderConnection, Task<UsageSnapshot?>> read) : IUsageProvider
    {
        public ProviderId Provider => ProviderId.OpenAi;

        public Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken) => read(connection);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
