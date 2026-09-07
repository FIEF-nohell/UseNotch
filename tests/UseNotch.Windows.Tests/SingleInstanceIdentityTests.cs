using UseNotch.Platform.Windows.SingleInstance;

namespace UseNotch.Windows.Tests;

public class SingleInstanceIdentityTests
{
    [Fact]
    public void Same_user_and_session_produce_a_stable_scoped_identity()
    {
        var first = SingleInstanceIdentity.Create("S-1-5-21-fixture", 4);
        var second = SingleInstanceIdentity.Create("S-1-5-21-fixture", 4);

        Assert.Equal(first, second);
        Assert.StartsWith(@"Local\UseNotch-", first.MutexName, StringComparison.Ordinal);
        Assert.StartsWith("UseNotch-", first.PipeName, StringComparison.Ordinal);
    }

    [Fact]
    public void User_or_session_change_isolated_identity()
    {
        var baseline = SingleInstanceIdentity.Create("S-1-5-21-fixture-a", 4);

        Assert.NotEqual(baseline, SingleInstanceIdentity.Create("S-1-5-21-fixture-b", 4));
        Assert.NotEqual(baseline, SingleInstanceIdentity.Create("S-1-5-21-fixture-a", 5));
    }
}
