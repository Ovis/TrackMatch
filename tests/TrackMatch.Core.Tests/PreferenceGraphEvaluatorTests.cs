using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>
/// Human Verdictから導出する優劣推移、Keep候補、循環検出の不変条件を検証する。
/// </summary>
public sealed class PreferenceGraphEvaluatorTests
{
    [Fact]
    public void GetKeepCandidates_RemovesDirectAndTransitiveInferiors()
    {
        var reviews = new[] { Confirmed(1, 2, 1), Confirmed(2, 3, 2) };
        Assert.Equal([1L], PreferenceGraphEvaluator.GetKeepCandidates(reviews, [1, 2, 3]));
        Assert.True(PreferenceGraphEvaluator.IsPreferredTransitively(reviews, 1, 3));
    }

    [Fact]
    public void GetKeepCandidates_AllowsMultipleTopCandidates()
    {
        var reviews = new[] { Confirmed(1, 2, 1), Confirmed(3, 2, 3) };
        Assert.Equal([1L, 3L], PreferenceGraphEvaluator.GetKeepCandidates(reviews, [1, 2, 3]));
    }

    [Fact]
    public void EnsureAcyclic_RejectsTransitiveCycle()
    {
        var reviews = new[] { Confirmed(1, 2, 1), Confirmed(2, 3, 2), Confirmed(1, 3, 3) };
        Assert.Throws<InvalidOperationException>(() => PreferenceGraphEvaluator.EnsureAcyclic(reviews));
    }

    private static CandidateReview Confirmed(long left, long right, long preferred)
        => new(CandidatePairKey.Create(left, right), CandidateReviewDecision.ConfirmedDuplicate, preferred, null);

    [Fact]
    public void GetKeepCandidates_ExternalPreferredTrackDoesNotEliminateOnlyLocalCandidate()
    {
        var reviews = new[]
        {
            new CandidateReview(CandidatePairKey.Create(1, 2), CandidateReviewDecision.ConfirmedDuplicate, 1, null),
        };

        Assert.Equal([2], PreferenceGraphEvaluator.GetKeepCandidates(reviews, [2]));
    }

    [Fact]
    public void GetKeepCandidates_UsesExternalTrackAsTransitivePathBetweenLocalCandidates()
    {
        var reviews = new[]
        {
            new CandidateReview(CandidatePairKey.Create(1, 2), CandidateReviewDecision.ConfirmedDuplicate, 1, null),
            new CandidateReview(CandidatePairKey.Create(2, 3), CandidateReviewDecision.ConfirmedDuplicate, 2, null),
        };

        Assert.Equal([1], PreferenceGraphEvaluator.GetKeepCandidates(reviews, [1, 3]));
    }
}
