using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.Application.Tests;

public class UsageTrendTests
{
    private static readonly DateTimeOffset Start = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_buffer_evicts_the_oldest_sample_once_it_is_full()
    {
        var store = new InMemoryUsageTrendStore();
        for (var index = 0; index <= InMemoryUsageTrendStore.Capacity; index++)
        {
            store.Record(ProviderId.OpenAi, "session", null, new QuotaSample(Start.AddMinutes(index), 0.01m * index));
        }

        // The oldest sample (index 0) fell out of the buffer, so the rate must be computed from sample
        // 1 onward rather than from the very first reading ever recorded.
        var estimate = store.Estimate(ProviderId.OpenAi, "session", Start.AddMinutes(InMemoryUsageTrendStore.Capacity));

        Assert.True(estimate.SufficientData);
        // From sample index 1 (1%) to index Capacity (Capacity%) over (Capacity - 1) minutes.
        var expectedPercentDelta = InMemoryUsageTrendStore.Capacity - 1;
        var expectedHours = (InMemoryUsageTrendStore.Capacity - 1) / 60.0;
        Assert.Equal(expectedPercentDelta / expectedHours, estimate.PercentPerHour, 3);
    }

    [Fact]
    public void A_changed_reset_time_clears_the_buffer_so_the_rate_never_spans_a_reset()
    {
        var store = new InMemoryUsageTrendStore();
        var firstReset = Start.AddHours(1);
        var secondReset = Start.AddHours(6);

        store.Record(ProviderId.OpenAi, "session", firstReset, new QuotaSample(Start, 0.9m));
        store.Record(ProviderId.OpenAi, "session", firstReset, new QuotaSample(Start.AddMinutes(30), 0.95m));
        // The window just rolled over: a new reset time with a lower reading.
        store.Record(ProviderId.OpenAi, "session", secondReset, new QuotaSample(Start.AddMinutes(31), 0.05m));

        var estimate = store.Estimate(ProviderId.OpenAi, "session", Start.AddMinutes(31));

        // Only one sample survived the rollover, so there is not enough data for a rate yet.
        Assert.False(estimate.SufficientData);
    }

    [Fact]
    public void A_rising_reading_produces_a_positive_rate_and_a_projected_time_to_limit()
    {
        var store = new InMemoryUsageTrendStore();
        store.Record(ProviderId.OpenAi, "session", null, new QuotaSample(Start, 0.10m));
        store.Record(ProviderId.OpenAi, "session", null, new QuotaSample(Start.AddHours(1), 0.30m));

        var estimate = store.Estimate(ProviderId.OpenAi, "session", Start.AddHours(1));

        Assert.True(estimate.SufficientData);
        Assert.Equal(20, estimate.PercentPerHour, 3);
        Assert.NotNull(estimate.EstimatedTimeToLimit);
        // 70 remaining percent at 20%/hour is 3.5 hours.
        Assert.Equal(3.5, estimate.EstimatedTimeToLimit!.Value.TotalHours, 3);
    }

    [Fact]
    public void A_flat_reading_has_sufficient_data_but_no_projected_time_to_limit()
    {
        var store = new InMemoryUsageTrendStore();
        store.Record(ProviderId.OpenAi, "session", null, new QuotaSample(Start, 0.4m));
        store.Record(ProviderId.OpenAi, "session", null, new QuotaSample(Start.AddHours(1), 0.4m));

        var estimate = store.Estimate(ProviderId.OpenAi, "session", Start.AddHours(1));

        Assert.True(estimate.SufficientData);
        Assert.Equal(0, estimate.PercentPerHour);
        Assert.Null(estimate.EstimatedTimeToLimit);
    }

    [Fact]
    public void A_declining_reading_has_sufficient_data_but_no_projected_time_to_limit()
    {
        var store = new InMemoryUsageTrendStore();
        store.Record(ProviderId.OpenAi, "session", null, new QuotaSample(Start, 0.6m));
        store.Record(ProviderId.OpenAi, "session", null, new QuotaSample(Start.AddHours(1), 0.4m));

        var estimate = store.Estimate(ProviderId.OpenAi, "session", Start.AddHours(1));

        Assert.True(estimate.SufficientData);
        Assert.True(estimate.PercentPerHour < 0);
        Assert.Null(estimate.EstimatedTimeToLimit);
    }

    [Fact]
    public void A_single_sample_is_never_enough_data()
    {
        var store = new InMemoryUsageTrendStore();
        store.Record(ProviderId.OpenAi, "session", null, new QuotaSample(Start, 0.4m));

        var estimate = store.Estimate(ProviderId.OpenAi, "session", Start);

        Assert.False(estimate.SufficientData);
        Assert.Null(estimate.EstimatedTimeToLimit);
    }

    [Fact]
    public void An_unknown_key_has_no_data_at_all()
    {
        var store = new InMemoryUsageTrendStore();

        var estimate = store.Estimate(ProviderId.OpenAi, "session", Start);

        Assert.False(estimate.SufficientData);
    }

    [Fact]
    public void A_fresh_store_instance_has_no_memory_of_a_prior_instances_samples()
    {
        var first = new InMemoryUsageTrendStore();
        first.Record(ProviderId.OpenAi, "session", null, new QuotaSample(Start, 0.1m));
        first.Record(ProviderId.OpenAi, "session", null, new QuotaSample(Start.AddHours(1), 0.9m));

        var second = new InMemoryUsageTrendStore();
        var estimate = second.Estimate(ProviderId.OpenAi, "session", Start.AddHours(1));

        Assert.False(estimate.SufficientData);
    }
}

public class UsageTrendPresenterTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Insufficient_data_describes_as_nothing()
        => Assert.Null(UsageTrendPresenter.Describe(BurnRateEstimate.Insufficient, null, Now));

    [Fact]
    public void A_null_estimate_describes_as_nothing()
        => Assert.Null(UsageTrendPresenter.Describe(null, null, Now));

    [Fact]
    public void A_flat_or_declining_rate_says_it_is_not_increasing()
    {
        var flat = new BurnRateEstimate(0, null, true);
        var declining = new BurnRateEstimate(-5, null, true);

        Assert.Equal("Not increasing this session", UsageTrendPresenter.Describe(flat, null, Now));
        Assert.Equal("Not increasing this session", UsageTrendPresenter.Describe(declining, null, Now));
    }

    [Fact]
    public void A_projection_that_lands_after_the_reset_reads_as_safe_until_reset()
    {
        var estimate = new BurnRateEstimate(5, TimeSpan.FromHours(10), true);
        var resetsAt = Now.AddHours(2);

        Assert.Equal("Safe until reset at this pace", UsageTrendPresenter.Describe(estimate, resetsAt, Now));
    }

    [Fact]
    public void A_projection_before_the_reset_states_the_estimated_time_to_limit()
    {
        var estimate = new BurnRateEstimate(20, TimeSpan.FromHours(3.5), true);
        var resetsAt = Now.AddHours(10);

        var description = UsageTrendPresenter.Describe(estimate, resetsAt, Now);

        Assert.NotNull(description);
        Assert.Contains("limit in", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_projection_with_no_known_reset_still_states_the_estimated_time_to_limit()
    {
        var estimate = new BurnRateEstimate(20, TimeSpan.FromHours(3.5), true);

        var description = UsageTrendPresenter.Describe(estimate, null, Now);

        Assert.NotNull(description);
        Assert.Contains("limit in", description, StringComparison.Ordinal);
    }
}
