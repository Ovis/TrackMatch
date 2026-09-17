using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictInvariantTests
{
    [Fact]
    public void HumanVerdict_IsAuthoritativeAndOnlyContentFailureInvalidatesIt()
    {
        Assert.True(HumanVerdictInvariant.HumanDecisionOverridesMachineEvidence);
        Assert.False(HumanVerdictInvariant.MachineEvidenceChangeInvalidatesHumanDecision);
        Assert.False(HumanVerdictInvariant.ReReviewRecommendationInvalidatesHumanDecision);
        Assert.True(HumanVerdictInvariant.ContentVerificationFailureInvalidatesHumanDecision);
        Assert.True(HumanVerdictInvariant.ContradictoryHumanDecisionsArePreservedAsConflict);
    }
}
