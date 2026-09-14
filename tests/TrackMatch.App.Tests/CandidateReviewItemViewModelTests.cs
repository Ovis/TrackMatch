using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// Candidate一覧でGlobal Human VerdictをLibrary固有Keepと混同せず表示することを検証する。
/// </summary>
public sealed class CandidateReviewItemViewModelTests
{
    [Fact]
    public void ReviewResult_ConfirmedDuplicateShowsOnlyGlobalVerdict()
    {
        var row = CreateRow(CandidateReviewDecision.ConfirmedDuplicate);

        var viewModel = new CandidateReviewItemViewModel(row);

        Assert.Equal("重複として確認済み", viewModel.ReviewResult);
    }

    private static CandidateReviewReportRow CreateRow(CandidateReviewDecision decision)
        => new(
            1,
            2,
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
