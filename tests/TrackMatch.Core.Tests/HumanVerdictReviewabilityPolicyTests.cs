using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictReviewabilityPolicyTests
{
    [Fact]
    public void ExistingVerdict_IsNotUnreviewed()
        => Assert.False(HumanVerdictReviewabilityPolicy.IsUnreviewed(CandidateReviewFactory.NotDuplicate(1, 2)));

    [Fact]
    public void Conflict_RequiresSeparateResolution()
        => Assert.True(HumanVerdictReviewabilityPolicy.RequiresConflictResolution(true));
}
