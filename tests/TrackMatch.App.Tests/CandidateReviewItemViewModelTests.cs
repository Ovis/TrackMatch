using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// Candidate一覧でGlobal Human Verdictと永続化しないレビュー省略状態を正しく表示することを検証する。
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

        var states = CandidateReviewPresentationStateResolver.Resolve([row], [group]);
        var viewModel = new CandidateReviewItemViewModel(
            row,
            states[CandidatePairKey.Create(2, 3)]);

        Assert.True(viewModel.IsReviewSkipped);
        Assert.False(viewModel.IsReviewed);
        Assert.Equal("レビュー省略", viewModel.ReviewResult);
        Assert.Contains("削除候補同士", viewModel.ReviewOriginText);
    }

    [Theory]
    [InlineData(DuplicateGroupKeepStatus.Unselected)]
    [InlineData(DuplicateGroupKeepStatus.Conflict)]
    [InlineData(DuplicateGroupKeepStatus.Missing)]
    public void Resolve_KeepIsNotSelected_DoesNotSkip(DuplicateGroupKeepStatus keepStatus)
    {
        var row = CreateRow(decision: null, trackIdA: 2, trackIdB: 3);
        var group = new DuplicateGroup(10, 1, null, keepStatus, [1, 2, 3], [1, 2, 3]);

        var states = CandidateReviewPresentationStateResolver.Resolve([row], [group]);

        Assert.False(states[CandidatePairKey.Create(2, 3)].IsReviewSkipped);
    }

    [Fact]
    public void Resolve_PairContainsKeep_DoesNotSkip()
    {
        var row = CreateRow(decision: null, trackIdA: 1, trackIdB: 2);
        var group = new DuplicateGroup(
            10,
            1,
            1,
            DuplicateGroupKeepStatus.Selected,
            [1, 2, 3],
            [1, 2, 3]);

        var states = CandidateReviewPresentationStateResolver.Resolve([row], [group]);

        Assert.False(states[CandidatePairKey.Create(1, 2)].IsReviewSkipped);
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

        var states = CandidateReviewPresentationStateResolver.Resolve([row], [group]);

        Assert.False(states[CandidatePairKey.Create(2, 3)].IsReviewSkipped);
    }

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
