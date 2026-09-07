using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using UseNotch.Domain;

namespace UseNotch.Application;

public sealed class ProviderReadException : Exception
{
    public ProviderReadException(
        string message,
        bool transient,
        bool schemaFailure = false,
        DateTimeOffset? serverDeadline = null,
        ErrorCategory category = ErrorCategory.Network,
        int? code = null,
        AuthenticationState? authenticationHint = null) : base(message)
    {
        IsTransient = transient;
        IsSchemaFailure = schemaFailure;
        ServerDeadline = serverDeadline;
        Category = schemaFailure ? ErrorCategory.Schema : category;
        Code = code;
        AuthenticationHint = authenticationHint;
        SafeMessage = schemaFailure ? "Provider response format is unsupported" : category switch
        {
            ErrorCategory.RateLimited => "Provider rate limit reached",
            ErrorCategory.Authentication => "Provider authentication failed",
            ErrorCategory.Forbidden => "Provider access denied",
            _ => "Provider request failed",
        };
    }

    public bool IsTransient { get; }
    public bool IsSchemaFailure { get; }
    public DateTimeOffset? ServerDeadline { get; }
    public ErrorCategory Category { get; }
    public int? Code { get; }
    public string SafeMessage { get; }

    // Null keeps the previously observed authentication state; providers set this only when they can
    // confidently classify the failure as missing, expired, rejected, access-denied, or unsupported.
    public AuthenticationState? AuthenticationHint { get; }
}

public sealed record CachedProviderState(int SchemaVersion, DateTimeOffset SavedAt, ProviderRuntimeState State);

public interface IUsageCache
{
    Task<ProviderRuntimeState?> LoadAsync(ProviderId provider, CancellationToken cancellationToken);
    Task SaveAsync(ProviderRuntimeState state, CancellationToken cancellationToken);
    Task ClearAsync(ProviderId provider, CancellationToken cancellationToken);
}

public sealed class JsonUsageCache(string rootDirectory, int currentSchemaVersion = 1) : IUsageCache
{
    private const long MaximumCacheBytes = 512 * 1024;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.General);

    public async Task<ProviderRuntimeState?> LoadAsync(ProviderId provider, CancellationToken cancellationToken)
    {
        var path = PathFor(provider);
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumCacheBytes)
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var entry = await JsonSerializer.DeserializeAsync<CachedProviderState>(stream, _jsonOptions, cancellationToken);
            return entry?.SchemaVersion == currentSchemaVersion ? entry.State with { Activity = null } : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public async Task SaveAsync(ProviderRuntimeState state, CancellationToken cancellationToken)
    {
        var path = PathFor(state.Connection.Provider);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(rootDirectory);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                // The cache holds normalized quota only. Activity is live, may carry a source-supplied
                // label, and must never be written to disk or restored as if it were a reading.
                var sanitized = state with { Activity = null };
                await JsonSerializer.SerializeAsync(stream, new CachedProviderState(currentSchemaVersion, DateTimeOffset.UtcNow, sanitized), _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task ClearAsync(ProviderId provider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(provider);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(ProviderId provider) => Path.Combine(rootDirectory, provider + ".json");
}

public static class RetryAfterParser
{
    public static DateTimeOffset? TryParseDeadline(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            return now.AddSeconds(seconds);
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
    }

    public static DateTimeOffset? TryParseDeadline(RetryConditionHeaderValue? header, DateTimeOffset now) =>
        header?.Delta is { } delta ? now.Add(delta) : header?.Date;
}

public static class BackoffPolicy
{
    public static TimeSpan LocalDelay(int consecutiveTransientFailures)
    {
        if (consecutiveTransientFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        var seconds = Math.Min(900, 60 * Math.Pow(2, consecutiveTransientFailures - 1));
        return TimeSpan.FromSeconds(seconds);
    }

    public static DateTimeOffset NextAttempt(DateTimeOffset now, int consecutiveTransientFailures, DateTimeOffset? serverDeadline) =>
        Max(now.Add(LocalDelay(consecutiveTransientFailures)), serverDeadline);

    public static DateTimeOffset SchemaNextAttempt(DateTimeOffset now, int consecutiveSchemaFailures) =>
        consecutiveSchemaFailures switch
        {
            1 => now.AddSeconds(60),
            2 => now.AddMinutes(5),
            _ => now.AddHours(1),
        };

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset? right) => right is { } value && value > left ? value : left;
}

public static class FreshnessPolicy
{
    public static DataFreshness Evaluate(DateTimeOffset lastSuccess, DateTimeOffset now)
    {
        var age = now - lastSuccess;
        if (age < TimeSpan.Zero)
        {
            return DataFreshness.Unknown;
        }

        if (age >= TimeSpan.FromHours(24))
        {
            return DataFreshness.Expired;
        }

        if (age >= TimeSpan.FromMinutes(15))
        {
            return DataFreshness.Stale;
        }

        return DataFreshness.Fresh;
    }

    public static ProviderRuntimeState Normalize(ProviderRuntimeState state, DateTimeOffset now)
    {
        var normalizedStatus = Normalize(state.Status, now);
        var snapshot = state.Snapshot;
        if (snapshot is not null && (normalizedStatus.Freshness == DataFreshness.Expired || snapshot.Headline?.ResetsAt <= now))
        {
            snapshot = new UsageSnapshot(snapshot.Provider, snapshot.Account, snapshot.RetrievedAt, snapshot.Windows, null, snapshot.Source, snapshot.Block);
        }

        return state with { Snapshot = snapshot, Status = normalizedStatus };
    }

    public static ProviderStatus Normalize(ProviderStatus status, DateTimeOffset now)
    {
        var freshness = status.LastSuccess is { } lastSuccess ? Evaluate(lastSuccess, now) : status.Freshness;
        return status with { Freshness = freshness };
    }
}
