using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictConflictSummaryResolverTests
{
    [Fact]
    public void Resolve_ReturnsConflictingTracksForLogicalContradiction()
    {
        CandidateReview[] verdicts = [CandidateReviewFactory.ConfirmedDuplicate(1, 2, 1), CandidateReviewFactory.ConfirmedDuplicate(2, 3, 2), CandidateReviewFactory.NotDuplicate(1, 3)];
        var summary = HumanVerdictConflictSummaryResolver.Resolve(verdicts, [1, 2, 3]);
        Assert.True(summary.IsConflict);
        Assert.Contains(1, summary.TrackIds);
        Assert.Contains(3, summary.TrackIds);
    }
}
