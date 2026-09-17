using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictPairStateResolverTests
{
    [Fact]
    public void Resolve_ConflictOverridesIndividualVerdict()
    {
        var verdict = CandidateReviewFactory.ConfirmedDuplicate(1, 2, 1);
        Assert.Equal(HumanVerdictPairState.Conflict, HumanVerdictPairStateResolver.Resolve(verdict, true));
    }

    [Fact]
    public void Resolve_UnreviewedWhenNoVerdict()
        => Assert.Equal(HumanVerdictPairState.Unreviewed, HumanVerdictPairStateResolver.Resolve(null, false));
}
