using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictResolverTests
{
    [Fact]
    public void Resolve_PreservesContradictoryVerdictsAndMarksConflict()
    {
        CandidateReview[] verdicts =
        [
            CandidateReviewFactory.ConfirmedDuplicate(1, 2, 1),
            CandidateReviewFactory.ConfirmedDuplicate(2, 3, 2),
            CandidateReviewFactory.NotDuplicate(1, 3),
        ];

        var result = HumanVerdictResolver.Resolve(verdicts);

        Assert.Equal(3, result.Verdicts.Count);
        Assert.True(result.HasConflict);
    }
}
