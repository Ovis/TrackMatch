using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictContentVerificationStateTests
{
    [Fact]
    public void Failed_IsDistinctFromReevaluationRecommendation()
    {
        Assert.NotEqual((int)HumanVerdictContentVerificationState.Failed, (int)HumanVerdictContentVerificationState.Verified);
        Assert.Equal(HumanVerdictReevaluationState.Recommended, HumanVerdictReevaluationState.Recommended);
    }
}
