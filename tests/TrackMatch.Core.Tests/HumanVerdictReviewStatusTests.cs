using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictReviewStatusTests
{
    [Fact]
    public void RecommendedVerdict_IsStillReviewed()
    {
        var status = new HumanVerdictReviewStatus(HumanVerdictPairState.NotDuplicate, HumanVerdictReevaluationState.Recommended, HumanVerdictConflictKind.None);
        Assert.True(status.IsReviewed);
        Assert.True(status.IsReReviewRecommended);
    }
}
