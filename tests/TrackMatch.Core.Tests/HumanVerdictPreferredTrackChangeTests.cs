using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictPreferredTrackChangeTests
{
    [Fact]
    public void Change_ProducesDifferentHumanVerdict()
    {
        var before = CandidateReviewFactory.ConfirmedDuplicate(1, 2, 1);
        var after = HumanVerdictPreferredTrackChange.Change(before, 2);
        Assert.NotEqual(before, after);
        Assert.Equal(2, after.PreferredTrackId);
    }
}
