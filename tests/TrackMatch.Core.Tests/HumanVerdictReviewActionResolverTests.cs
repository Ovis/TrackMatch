using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictReviewActionResolverTests
{
    [Fact]
    public void PreferB_StoresBAsPreferredTrack()
    {
        var verdict = HumanVerdictReviewActionResolver.Resolve(CandidatePairKey.Create(1, 2), HumanVerdictReviewAction.ConfirmDuplicatePreferB);
        Assert.Equal(2, verdict!.PreferredTrackId);
    }

    [Fact]
    public void Clear_ReturnsNoCurrentVerdict()
        => Assert.Null(HumanVerdictReviewActionResolver.Resolve(CandidatePairKey.Create(1, 2), HumanVerdictReviewAction.Clear));
}
