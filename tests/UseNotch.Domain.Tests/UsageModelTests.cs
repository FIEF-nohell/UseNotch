using UseNotch.Domain;

namespace UseNotch.Domain.Tests;

public class UsageModelTests
{
    [Fact]
    public void Missing_headline_does_not_promote_a_secondary_window()
    {
        var window = new QuotaWindow("weekly", "Weekly", TimeSpan.FromDays(7), null, null, new UsageLimit(null, 10, 10, null, "requests"));
        var snapshot = new UsageSnapshot(ProviderId.OpenAi, new AccountScope("test", IdentityConfidence.Unknown), DateTimeOffset.UtcNow, [window], null, new SourceDescriptor("fixture", "M04", false), null);

        Assert.Null(snapshot.Headline);
    }

    [Fact]
    public void Invalid_negative_usage_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageLimit(-1, null, null, null, "requests"));
    }

    [Fact]
    public void Over_limit_fraction_is_preserved_for_presentation_clamping()
    {
        var limit = new UsageLimit(12, null, 10, 1.2m, "requests");

        Assert.Equal(1.2m, limit.UsedFraction);
    }
}
