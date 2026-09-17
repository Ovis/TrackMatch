using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictContentIdentityTests
{
    [Fact]
    public void Matches_IgnoresTimestampOnlyChange()
    {
        var before = new HumanVerdictContentIdentity(100, 1, 300);
        var after = new HumanVerdictContentIdentity(100, 2, 300);
        Assert.True(before.Matches(after));
    }

    [Fact]
    public void Matches_RejectsSizeOrDurationChange()
        => Assert.False(new HumanVerdictContentIdentity(100, 1, 300).Matches(new HumanVerdictContentIdentity(101, 2, 300)));
}
