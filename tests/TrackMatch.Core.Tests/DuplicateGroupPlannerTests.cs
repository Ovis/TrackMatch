using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>
/// Global ConfirmedDuplicateの連結関係から重複グループを再構成する規則を検証する。
/// </summary>
public sealed class DuplicateGroupPlannerTests
{
    [Fact]
    public void Build_CreatesGlobalGroupFromConfirmedPair()
    {
        var reviews = new[] { Confirmed(1, 2, 1) };

        var group = Assert.Single(DuplicateGroupPlanner.Build(reviews, []));

        Assert.Null(group.ExistingGroupId);
        Assert.Equal(new long[] { 1, 2 }, group.TrackIds);
    }

    [Fact]
    public void Build_AddingTrackRetainsExistingGroupId()
    {
        var existing = new GlobalDuplicateGroup(12, [1, 2]);
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(2, 3, 2),
        };

        var group = Assert.Single(DuplicateGroupPlanner.Build(reviews, [existing]));

        Assert.Equal(12, group.ExistingGroupId);
        Assert.Equal(new long[] { 1, 2, 3 }, group.TrackIds);
    }

    [Fact]
    public void Build_HumanKeepSelectionDoesNotAffectGlobalTopology()
    {
        var existing = new GlobalDuplicateGroup(12, [1, 2, 3]);
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(2, 3, 2),
            Confirmed(1, 3, 3),
        };

        var group = Assert.Single(DuplicateGroupPlanner.Build(reviews, [existing]));

        Assert.Equal(12, group.ExistingGroupId);
        Assert.Equal(new long[] { 1, 2, 3 }, group.TrackIds);
    }

    [Fact]
    public void Build_MergesExistingGroupsAndReusesLargestOverlapGroupId()
    {
        var existing = new[]
        {
            new GlobalDuplicateGroup(10, [1, 2]),
            new GlobalDuplicateGroup(20, [3, 4]),
        };
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(3, 4, 3),
            Confirmed(2, 3, 2),
        };

        var group = Assert.Single(DuplicateGroupPlanner.Build(reviews, existing));

        Assert.Equal(10, group.ExistingGroupId);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, group.TrackIds);
    }

    [Fact]
    public void Build_RemovingBridgeReusesExistingIdForOnlyOneSplitComponent()
    {
        var existing = new GlobalDuplicateGroup(12, [1, 2, 3]);
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(4, 5, 4),
        };

        var groups = DuplicateGroupPlanner.Build(reviews, [existing]);

        Assert.Equal(2, groups.Count);
        var first = Assert.Single(groups, group => group.TrackIds.Contains(1));
        Assert.Equal(12, first.ExistingGroupId);
        Assert.Equal(new long[] { 1, 2 }, first.TrackIds);
        var second = Assert.Single(groups, group => group.TrackIds.Contains(4));
        Assert.Null(second.ExistingGroupId);
        Assert.Equal(new long[] { 4, 5 }, second.TrackIds);
    }

    [Fact]
    public void Build_SplitReusesExistingIdForLargestOverlapComponent()
    {
        var existing = new GlobalDuplicateGroup(12, [1, 2, 3, 4, 5]);
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(3, 4, 3),
            Confirmed(4, 5, 4),
        };

        var groups = DuplicateGroupPlanner.Build(reviews, [existing]);

        var smaller = Assert.Single(groups, group => group.TrackIds.Contains(1));
        Assert.Null(smaller.ExistingGroupId);
        var larger = Assert.Single(groups, group => group.TrackIds.Contains(3));
        Assert.Equal(12, larger.ExistingGroupId);
        Assert.Equal(new long[] { 3, 4, 5 }, larger.TrackIds);
    }

    [Fact]
    public void Build_SplitComponentWithoutReusableOldIdGetsNewGroup()
    {
        var existing = new GlobalDuplicateGroup(12, [1, 2, 3, 4]);
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(3, 4, 3),
        };

        var groups = DuplicateGroupPlanner.Build(reviews, [existing]);

        Assert.Equal(2, groups.Count);
        Assert.Single(groups, group => group.ExistingGroupId == 12);
        Assert.Single(groups, group => group.ExistingGroupId is null);
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
