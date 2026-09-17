using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictHistoryPolicyTests
{
    [Fact]
    public void PreferredTrackChange_IsVerdictChange()
    {
        var before = CandidateReviewFactory.ConfirmedDuplicate(1, 2, 1);
        var after = CandidateReviewFactory.ConfirmedDuplicate(1, 2, 2);
        Assert.True(HumanVerdictHistoryPolicy.HasChanged(before, after));
    }
}
