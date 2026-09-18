using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>
/// Keep確定のための補完Candidate生成条件を検証する。
/// </summary>
public sealed class ReviewNecessityEvaluatorTests
{
    [Fact]
    public void FindSupplementalPair_TwoTopCandidatesWithoutPair_ReturnsOnePair()
    {
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(3, 2, 3),
        };

        var pair = ReviewNecessityEvaluator.FindSupplementalPair([1, 2, 3], reviews, []);

        Assert.Equal(CandidatePairKey.Create(1, 3), pair);
    }

    [Fact]
    public void FindSupplementalPair_ExistingUnreviewedTopPair_DoesNotCreateAnother()
    {
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(3, 2, 3),
        };
        var pairs = new[] { new CandidatePair(1, 3, 0) };

        Assert.Null(ReviewNecessityEvaluator.FindSupplementalPair([1, 2, 3], reviews, pairs));
    }

    [Fact]
    public void FindSupplementalPair_UniqueKeep_DoesNotCreatePair()
    {
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(1, 3, 1),
        };

        Assert.Null(ReviewNecessityEvaluator.FindSupplementalPair([1, 2, 3], reviews, []));
    }

    [Fact]
    public void FindSupplementalPair_MultipleTopCandidates_CreatesOnlyOnePair()
    {
        var reviews = new[]
        {
            Confirmed(1, 4, 1),
            Confirmed(2, 4, 2),
            Confirmed(3, 4, 3),
        };

        var pair = ReviewNecessityEvaluator.FindSupplementalPair([1, 2, 3, 4], reviews, []);

        Assert.Equal(CandidatePairKey.Create(1, 2), pair);
    }

    private static CandidateReview Confirmed(long a, long b, long preferred)
        => new(CandidatePairKey.Create(a, b), CandidateReviewDecision.ConfirmedDuplicate, preferred, null);
}
