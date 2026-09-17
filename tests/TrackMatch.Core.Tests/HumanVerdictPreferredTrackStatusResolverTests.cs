using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictPreferredTrackStatusResolverTests
{
    [Fact]
    public void Resolve_ReturnsConflictForDifferentPreferences()
    {
        CandidateReview[] verdicts = [CandidateReviewFactory.ConfirmedDuplicate(1, 2, 1), CandidateReviewFactory.ConfirmedDuplicate(2, 3, 3)];
        var result = HumanVerdictPreferredTrackStatusResolver.Resolve(verdicts, [1, 2, 3]);
        Assert.Equal(HumanVerdictPreferredTrackStatus.Conflict, result.Status);
    }
}
