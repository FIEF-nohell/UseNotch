using System.Net.Http.Headers;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.Application.Tests;

public class CacheAndBackoffTests
{
    [Fact]
    public async Task Json_cache_round_trips_sanitized_state_and_recovers_corruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "usenotch-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new JsonUsageCache(root);
            var state = new ProviderRuntimeState(new ProviderConnection(ProviderId.OpenAi, "fixture", true, 2), null,
                new ProviderStatus(AuthenticationState.Authenticated, DataFreshness.Fresh, null, null, null, false, null), 3, 4);
            await cache.SaveAsync(state, CancellationToken.None);
            Assert.Equal(state, await cache.LoadAsync(ProviderId.OpenAi, CancellationToken.None));

            await File.WriteAllTextAsync(Path.Combine(root, "OpenAi.json"), "{not json");
            Assert.Null(await cache.LoadAsync(ProviderId.OpenAi, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Future_cache_schema_is_ignored()
    {
        var root = Path.Combine(Path.GetTempPath(), "usenotch-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "OpenAi.json"), "{\"SchemaVersion\":99}");
            Assert.Null(await new JsonUsageCache(root).LoadAsync(ProviderId.OpenAi, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Old_cache_schema_is_ignored()
    {
        var root = Path.Combine(Path.GetTempPath(), "usenotch-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "OpenAi.json"), "{\"SchemaVersion\":0}");
            Assert.Null(await new JsonUsageCache(root, 1).LoadAsync(ProviderId.OpenAi, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("60", 60)]
    public void Retry_after_seconds_are_parsed(string header, int seconds)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddSeconds(seconds), RetryAfterParser.TryParseDeadline(header, now));
    }

    [Fact]
    public void Retry_after_date_and_long_server_deadline_are_preserved()
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        var deadline = now.AddHours(2);

        Assert.Equal(deadline, RetryAfterParser.TryParseDeadline(deadline.ToString("R"), now));
        Assert.Equal(deadline, BackoffPolicy.NextAttempt(now, 1, deadline));
        Assert.Equal(TimeSpan.FromMinutes(15), BackoffPolicy.LocalDelay(10));
    }

    [Fact]
    public void Http_retry_after_header_supports_delta_and_date()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddSeconds(30), RetryAfterParser.TryParseDeadline(new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)), now));
    }

    [Fact]
    public void Schema_failures_progress_to_an_hourly_cooldown()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddSeconds(60), BackoffPolicy.SchemaNextAttempt(now, 1));
        Assert.Equal(now.AddMinutes(5), BackoffPolicy.SchemaNextAttempt(now, 2));
        Assert.Equal(now.AddHours(1), BackoffPolicy.SchemaNextAttempt(now, 3));
    }

    [Fact]
    public async Task Disk_write_failures_are_recoverable()
    {
        var file = Path.Combine(Path.GetTempPath(), "usenotch-cache-file-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(file, "not a directory");
        try
        {
            var state = new ProviderRuntimeState(new ProviderConnection(ProviderId.OpenAi, "fixture", true, 1), null,
                new ProviderStatus(AuthenticationState.Authenticated, DataFreshness.Fresh, null, null, null, false, null), 0, 0);
            await new JsonUsageCache(file).SaveAsync(state, CancellationToken.None);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(0, DataFreshness.Fresh)]
    [InlineData(901, DataFreshness.Stale)]
    [InlineData(86400, DataFreshness.Expired)]
    public void Freshness_uses_last_success_age(int ageSeconds, DataFreshness expected)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(expected, FreshnessPolicy.Evaluate(now.AddSeconds(-ageSeconds), now));
    }
}
