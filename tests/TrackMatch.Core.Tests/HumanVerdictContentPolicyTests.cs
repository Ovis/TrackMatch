using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictContentPolicyTests
{
    [Fact]
    public void VerificationFailure_RequiresInvalidation()
        => Assert.True(HumanVerdictContentPolicy.RequiresInvalidation(HumanVerdictContentVerificationState.Failed));

    [Fact]
    public void ReReviewRecommended_DoesNotInvalidateVerdict()
        => Assert.True(HumanVerdictContentPolicy.KeepsVerdict(HumanVerdictReevaluationState.Recommended));
}
