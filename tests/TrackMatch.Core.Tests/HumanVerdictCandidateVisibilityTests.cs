using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictCandidateVisibilityTests
{
    [Fact]
    public void ReReviewRecommended_RemainsReviewed()
    {
        var verdict = CandidateReviewFactory.NotDuplicate(1, 2);
        Assert.True(HumanVerdictCandidateVisibility.IsReviewed(verdict));
        Assert.True(HumanVerdictCandidateVisibility.IsReReviewRecommended(verdict, HumanVerdictReevaluationState.Recommended));
    }
}
