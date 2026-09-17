using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictMissingTrackPolicyTests
{
    [Fact]
    public void Missing_KeepsVerdictButExcludesMaterializedGroup()
    {
        Assert.True(HumanVerdictMissingTrackPolicy.KeepsCurrentVerdict());
        Assert.True(HumanVerdictMissingTrackPolicy.ExcludesFromMaterializedGroup());
    }
}
