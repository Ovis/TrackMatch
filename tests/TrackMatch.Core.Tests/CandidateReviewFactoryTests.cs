using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>UI等の呼び出し元がHuman Verdictの不変条件を崩さないことを検証する。</summary>
public sealed class CandidateReviewFactoryTests
{
    [Fact]
    public void NotDuplicate_ClearsPreferredTrack()
    {
        var review = CandidateReviewFactory.NotDuplicate(2, 1);
        Assert.Equal(CandidateReviewDecision.NotDuplicate, review.Decision);
        Assert.Null(review.PreferredTrackId);
        Assert.Equal(new CandidatePairKey(1, 2), review.Pair);
    }

    [Fact]
    public void ConfirmedDuplicate_PreservesPreferredTrackAfterPairNormalization()
    {
        var review = CandidateReviewFactory.ConfirmedDuplicate(2, 1, 2);
        Assert.Equal(CandidateReviewDecision.ConfirmedDuplicate, review.Decision);
        Assert.Equal(2, review.PreferredTrackId);
        Assert.Equal(new CandidatePairKey(1, 2), review.Pair);
    }

    [Fact]
    public void ConfirmedDuplicate_RejectsTrackOutsidePair()
        => Assert.Throws<InvalidOperationException>(() => CandidateReviewFactory.ConfirmedDuplicate(1, 2, 3));
}
