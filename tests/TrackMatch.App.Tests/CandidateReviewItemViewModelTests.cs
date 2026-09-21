using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// 候補一覧で共有レビュー判定と永続化しないレビュー省略状態を正しく表示することを検証する。
/// </summary>
public sealed class CandidateReviewItemViewModelTests
{
    [Fact]
    public void ReviewResult_ConfirmedDuplicateShowsOnlyGlobalVerdict()
    {
        var row = CreateRow(CandidateReviewDecision.ConfirmedDuplicate);

        var viewModel = new CandidateReviewItemViewModel(row);

        Assert.Equal("重複として確認済", viewModel.ReviewResult);
    }

    [Fact]
    public void Resolve_NonKeepPairInSelectedGroup_IsReviewSkipped()
    {
        var row = CreateRow(decision: null, trackIdA: 2, trackIdB: 3);
        var group = new DuplicateGroup(
            10,
            1,
            1,
            DuplicateGroupKeepStatus.Selected,
            [1, 2, 3],
            [1, 2, 3]);

        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(1, 3, 1),
        };
        var states = CandidateReviewPresentationStateResolver.Resolve([row], [group], reviews);
        var viewModel = new CandidateReviewItemViewModel(
            row,
            states[CandidatePairKey.Create(2, 3)]);

        Assert.True(viewModel.IsReviewSkipped);
        Assert.False(viewModel.IsReviewed);
        Assert.Equal("レビュー省略", viewModel.ReviewResult);
        Assert.Contains("残すファイルの決定には不要", viewModel.ReviewOriginText);
    }

    [Fact]
    public void Resolve_MultipleKeepCandidates_RequiredTopPairDoesNotSkip()
    {
        var row = CreateRow(decision: null, trackIdA: 1, trackIdB: 3);
        var group = new DuplicateGroup(10, 1, null, DuplicateGroupKeepStatus.Unselected, [1, 2, 3], [1, 2, 3]);
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(3, 2, 3),
        };

        var states = CandidateReviewPresentationStateResolver.Resolve([row], [group], reviews);

        Assert.False(states[CandidatePairKey.Create(1, 3)].IsReviewSkipped);
    }

    [Fact]
    public void Resolve_ConflictGroup_DoesNotSkipOtherwiseTransitivePair()
    {
        var row = CreateRow(decision: null, trackIdA: 1, trackIdB: 4);
        var group = new DuplicateGroup(10, 1, null, DuplicateGroupKeepStatus.Conflict, [1, 2, 3, 4], [1, 2, 3, 4]);
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(2, 3, 2),
            Confirmed(3, 4, 3),
            new CandidateReview(CandidatePairKey.Create(1, 3), CandidateReviewDecision.NotDuplicate, null, null),
        };

        var states = CandidateReviewPresentationStateResolver.Resolve([row], [group], reviews);

        Assert.False(states[CandidatePairKey.Create(1, 4)].IsReviewSkipped);
    }

    [Fact]
    public void Resolve_MissingKeepState_DoesNotSkip()
    {
        var row = CreateRow(decision: null, trackIdA: 2, trackIdB: 3);
        var group = new DuplicateGroup(10, 1, null, DuplicateGroupKeepStatus.Missing, [1, 2, 3], [1, 2, 3]);

        var states = CandidateReviewPresentationStateResolver.Resolve([row], [group], []);

        Assert.False(states[CandidatePairKey.Create(2, 3)].IsReviewSkipped);
    }

    [Fact]
    public void Resolve_UniqueKeep_AllRemainingPairsCanSkip()
    {
        var row = CreateRow(decision: null, trackIdA: 1, trackIdB: 3);
        var group = new DuplicateGroup(
            10,
            1,
            1,
            DuplicateGroupKeepStatus.Selected,
            [1, 2, 3],
            [1, 2, 3]);
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(2, 3, 2),
        };

        var states = CandidateReviewPresentationStateResolver.Resolve([row], [group], reviews);

        Assert.True(states[CandidatePairKey.Create(1, 3)].IsReviewSkipped);
    }

    [Fact]
    public void Resolve_ExistingHumanVerdict_DoesNotSkip()
    {
        var row = CreateRow(CandidateReviewDecision.ConfirmedDuplicate, 2, 3);
        var group = new DuplicateGroup(
            10,
            1,
            1,
            DuplicateGroupKeepStatus.Selected,
            [1, 2, 3],
            [1, 2, 3]);

        var states = CandidateReviewPresentationStateResolver.Resolve([row], [group], []);

        Assert.False(states[CandidatePairKey.Create(2, 3)].IsReviewSkipped);
    }

    private static CandidateReview Confirmed(long left, long right, long preferredTrackId)
        => new(CandidatePairKey.Create(left, right), CandidateReviewDecision.ConfirmedDuplicate, preferredTrackId, null);

    private static CandidateReviewReportRow CreateRow(
        CandidateReviewDecision? decision,
        long trackIdA = 1,
        long trackIdB = 2)
        => new(
            trackIdA,
            trackIdB,
            null,
            null,
            0.99,
            1,
            1,
            1,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(3),
            "a.flac",
            "b.flac",
            ["Artist"],
            ["Artist"],
            "A",
            "B",
            "Album",
            "Album",
            [],
            [],
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMinutes(3),
            100,
            100,
            "FLAC",
            "FLAC",
            "FLAC",
            "FLAC",
            900,
            900,
            44100,
            44100,
            16,
            16,
            2,
            2,
            decision);
}
