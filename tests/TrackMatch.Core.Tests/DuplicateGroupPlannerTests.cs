using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;

namespace TrackMatch.Core.Tests;

/// <summary>
/// ConfirmedDuplicateの連結関係から重複グループを再構成する規則を検証する。
/// </summary>
public sealed class DuplicateGroupPlannerTests
{
    [Fact]
    public void Build_CreatesGroupFromConfirmedPair()
    {
        var reviews = new[] { Confirmed(1, 2, 1) };

        var group = Assert.Single(DuplicateGroupPlanner.Build(reviews, []));

        Assert.Null(group.ExistingGroupId);
        Assert.Equal(1, group.KeepTrackId);
        Assert.Equal(new long[] { 1, 2 }, group.TrackIds);
    }

    [Fact]
    public void Build_AddingTrackUsesCurrentExplicitKeepForWholeGroup()
    {
        var existing = new DuplicateGroup(12, 1, 1, [1, 2]);
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(2, 3, 2),
        };

        var group = Assert.Single(DuplicateGroupPlanner.Build(reviews, [existing], preferredKeepTrackId: 2));

        Assert.Equal(12, group.ExistingGroupId);
        Assert.Equal(2, group.KeepTrackId);
        Assert.Equal(new long[] { 1, 2, 3 }, group.TrackIds);
    }

    [Fact]
    public void Build_LaterReviewCanChangeKeepToThirdTrack()
    {
        var existing = new DuplicateGroup(12, 1, 2, [1, 2, 3]);
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(2, 3, 2),
            Confirmed(1, 3, 3),
        };

        var group = Assert.Single(DuplicateGroupPlanner.Build(reviews, [existing], preferredKeepTrackId: 3));

        Assert.Equal(3, group.KeepTrackId);
        Assert.Equal(new long[] { 1, 2, 3 }, group.TrackIds);
    }

    [Fact]
    public void Build_MergesExistingGroupsAndUsesCurrentReviewKeep()
    {
        var existing = new[]
        {
            new DuplicateGroup(10, 1, 1, [1, 2]),
            new DuplicateGroup(20, 1, 3, [3, 4]),
        };
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(3, 4, 3),
            Confirmed(2, 3, 2),
        };

        var group = Assert.Single(DuplicateGroupPlanner.Build(reviews, existing, preferredKeepTrackId: 2));

        Assert.Equal(2, group.KeepTrackId);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, group.TrackIds);
    }

    [Fact]
    public void Build_RemovingBridgeSplitsGroupAndPreservesValidKeepSide()
    {
        var existing = new DuplicateGroup(12, 1, 1, [1, 2, 3]);
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(4, 5, 4),
        };

        var groups = DuplicateGroupPlanner.Build(reviews, [existing]);

        Assert.Equal(2, groups.Count);
        var first = Assert.Single(groups.Where(group => group.TrackIds.Contains(1)));
        Assert.Equal(12, first.ExistingGroupId);
        Assert.Equal(1, first.KeepTrackId);
        Assert.Equal(new long[] { 1, 2 }, first.TrackIds);
        var second = Assert.Single(groups.Where(group => group.TrackIds.Contains(4)));
        Assert.Null(second.ExistingGroupId);
        Assert.Equal(4, second.KeepTrackId);
    }

    [Fact]
    public void Build_SplitSideWithoutOldKeepStillReceivesKeep()
    {
        var existing = new DuplicateGroup(12, 1, 3, [1, 2, 3]);
        var reviews = new[] { Confirmed(1, 2, 2) };

        var group = Assert.Single(DuplicateGroupPlanner.Build(reviews, [existing]));

        Assert.Null(group.ExistingGroupId);
        Assert.Equal(2, group.KeepTrackId);
        Assert.Equal(new long[] { 1, 2 }, group.TrackIds);
    }

    [Fact]
    public void Build_RejectsNotDuplicateInsideTransitiveConfirmedComponent()
    {
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(2, 3, 2),
            new CandidateReview(CandidatePairKey.Create(1, 3), CandidateReviewDecision.NotDuplicate, null),
        };

        var exception = Assert.Throws<InvalidOperationException>(() => DuplicateGroupPlanner.Build(reviews, []));

        Assert.Contains("矛盾", exception.Message);
    }

    private static CandidateReview Confirmed(long left, long right, long keep)
        => new(CandidatePairKey.Create(left, right), CandidateReviewDecision.ConfirmedDuplicate, null, keep);
}
