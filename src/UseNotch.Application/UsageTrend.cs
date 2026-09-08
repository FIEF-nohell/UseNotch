using System.Globalization;
using UseNotch.Domain;

namespace UseNotch.Application;

/// <summary>
/// One recorded reading for a quota window, at a point in time. <see cref="UsedFraction"/> mirrors
/// <see cref="Domain.UsageLimit.UsedFraction"/> and stays nullable for the same reason: a window can be
/// reported without a usable fraction.
/// </summary>
public sealed record QuotaSample(DateTimeOffset SampledAt, decimal? UsedFraction);

/// <summary>
/// A session-local burn-rate projection for one quota window. Nothing here is persisted or shared across
/// app instances; it exists only to describe "at this pace, when will this window run out" while the
/// app is running.
/// </summary>
public sealed record BurnRateEstimate(double PercentPerHour, TimeSpan? EstimatedTimeToLimit, bool SufficientData)
{
    public static BurnRateEstimate Insufficient { get; } = new(0, null, false);
}

/// <summary>
/// Records session-local samples for a provider's quota windows and estimates a burn rate from them.
/// Implementations never touch disk or the network; a fresh instance has no memory of any prior one.
/// </summary>
public interface IUsageTrendStore
{
    /// <summary>
    /// Records one reading for the given provider and window. <paramref name="resetsAt"/> is the
    /// window's current reset time; when it changes from the value recorded with the previous sample for
    /// this same key, the buffer is cleared first so a burn rate never spans a reset.
    /// </summary>
    void Record(ProviderId provider, string windowId, DateTimeOffset? resetsAt, QuotaSample sample);

    /// <summary>Estimates the burn rate for a window from whatever samples are currently buffered.</summary>
    BurnRateEstimate Estimate(ProviderId provider, string windowId, DateTimeOffset now);
}

/// <summary>
/// An in-memory, per-process ring buffer keyed by (provider, window id). Bounded at
/// <see cref="Capacity"/> samples per key so a long-running session cannot grow this without limit.
/// </summary>
public sealed class InMemoryUsageTrendStore : IUsageTrendStore
{
    /// <summary>
    /// Enough samples to see a real trend across several polling cycles without keeping more history
    /// than a session-local estimate needs.
    /// </summary>
    public const int Capacity = 24;

    private readonly object _gate = new();
    private readonly Dictionary<(ProviderId Provider, string WindowId), Bucket> _buckets = [];

    public void Record(ProviderId provider, string windowId, DateTimeOffset? resetsAt, QuotaSample sample)
    {
        var key = (provider, windowId);
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket();
                _buckets[key] = bucket;
            }

            // A reset time that changed since the last sample means the window rolled over. Whatever was
            // buffered belonged to the window that just ended, so a burn rate computed across it would
            // describe a jump that never really happened.
            if (bucket.LastResetsAt is { } previous && resetsAt != previous)
            {
                bucket.Samples.Clear();
            }

            bucket.LastResetsAt = resetsAt;
            bucket.Samples.Enqueue(sample);
            while (bucket.Samples.Count > Capacity)
            {
                bucket.Samples.Dequeue();
            }
        }
    }

    public BurnRateEstimate Estimate(ProviderId provider, string windowId, DateTimeOffset now)
    {
        var key = (provider, windowId);
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                return BurnRateEstimate.Insufficient;
            }

            return Compute(bucket.Samples);
        }
    }

    private static BurnRateEstimate Compute(IReadOnlyCollection<QuotaSample> samples)
    {
        QuotaSample? earliest = null;
        QuotaSample? latest = null;
        foreach (var sample in samples)
        {
            if (sample.UsedFraction is null)
            {
                continue;
            }

            earliest ??= sample;
            latest = sample;
        }

        if (earliest is null || latest is null || ReferenceEquals(earliest, latest))
        {
            return BurnRateEstimate.Insufficient;
        }

        var elapsed = latest.SampledAt - earliest.SampledAt;
        if (elapsed <= TimeSpan.Zero)
        {
            return BurnRateEstimate.Insufficient;
        }

        var percentDelta = (double)((latest.UsedFraction ?? 0) - (earliest.UsedFraction ?? 0)) * 100;
        var percentPerHour = percentDelta / elapsed.TotalHours;

        if (percentPerHour <= 0)
        {
            return new BurnRateEstimate(percentPerHour, null, true);
        }

        var remainingPercent = 100 - (double)(latest.UsedFraction ?? 0) * 100;
        var hoursToLimit = remainingPercent / percentPerHour;
        return new BurnRateEstimate(percentPerHour, TimeSpan.FromHours(Math.Max(0, hoursToLimit)), true);
    }

    private sealed class Bucket
    {
        public Queue<QuotaSample> Samples { get; } = new(Capacity);

        public DateTimeOffset? LastResetsAt { get; set; }
    }
}

/// <summary>
/// Turns a <see cref="BurnRateEstimate"/> into the one line of text a user reads. Kept separate from any
/// view model so the phrasing rules are testable without a window.
/// </summary>
public static class UsageTrendPresenter
{
    /// <summary>
    /// Placed once by the caller, not repeated per window: the estimate only reflects readings taken
    /// since the app started, and starts over on the next launch.
    /// </summary>
    public const string SessionEstimateCaveat = "Estimate, this session only. Resets on app restart.";

    public static string? Describe(BurnRateEstimate? estimate, DateTimeOffset? windowResetsAt, DateTimeOffset now)
    {
        if (estimate is not { SufficientData: true } value)
        {
            return null;
        }

        if (value.PercentPerHour <= 0)
        {
            return "Not increasing this session";
        }

        if (value.EstimatedTimeToLimit is not { } timeToLimit)
        {
            return null;
        }

        if (windowResetsAt is { } resetsAt && now + timeToLimit >= resetsAt)
        {
            return "Safe until reset at this pace";
        }

        return string.Create(CultureInfo.InvariantCulture, $"At this pace, limit in {DescribeDuration(timeToLimit)}");
    }

    private static string DescribeDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return "under a minute";
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, Math.Round(duration.TotalMinutes)):0} min");
        }

        if (duration < TimeSpan.FromHours(48))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(duration.TotalHours):0} h");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(duration.TotalDays):0} d");
    }
}
