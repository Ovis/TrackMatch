using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictPairComparerTests
{
    [Fact]
    public void IsSamePair_NormalizesInputOrder()
        => Assert.True(HumanVerdictPairComparer.IsSamePair(CandidateReviewFactory.NotDuplicate(1, 2), 2, 1));
}
