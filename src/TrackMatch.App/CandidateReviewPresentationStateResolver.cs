using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;

namespace TrackMatch.App;

/// <summary>
/// Candidate Review Reportと現在のLibrary Duplicate Groupから、永続化しないUI上のレビュー状態を導出する。
/// </summary>
internal static class CandidateReviewPresentationStateResolver
{
    internal const string ReviewSkipReason = "この2曲は同じ重複グループの削除候補同士であるため、レビューを省略しています";

    /// <summary>
    /// Candidateごとのレビュー省略状態を計算する。
    /// </summary>
    /// <remarks>
    /// レビュー省略はHuman Verdictではなく、現在のGroup Keepから導出する表示状態である。
    /// DBへ保存しないことで、Keep変更やGroup再構成後に古い省略判定が残らないようにする。
    /// </remarks>
    /// <param name="rows">現在LibraryのCandidate Review Report</param>
    /// <param name="groups">現在LibraryへMaterializeされたDuplicate Group</param>
    internal static IReadOnlyDictionary<CandidatePairKey, CandidateReviewPresentationState> Resolve(
        IReadOnlyCollection<CandidateReviewReportRow> rows,
        IReadOnlyCollection<DuplicateGroup> groups)
    {
        var groupByTrackId = groups
            .SelectMany(group => group.TrackIds.Select(trackId => (trackId, group)))
            .ToDictionary(item => item.trackId, item => item.group);
        var result = new Dictionary<CandidatePairKey, CandidateReviewPresentationState>(rows.Count);

        foreach (var row in rows)
        {
            var pair = CandidatePairKey.Create(row.TrackIdA, row.TrackIdB);
            var isSkipped = row.ReviewDecision is null
                && groupByTrackId.TryGetValue(row.TrackIdA, out var groupA)
                && groupByTrackId.TryGetValue(row.TrackIdB, out var groupB)
                && groupA.Id == groupB.Id
                && groupA.KeepStatus == DuplicateGroupKeepStatus.Selected
                && groupA.KeepTrackId is { } keepTrackId
                && keepTrackId != row.TrackIdA
                && keepTrackId != row.TrackIdB;

            result[pair] = isSkipped
                ? new CandidateReviewPresentationState(true, ReviewSkipReason)
                : CandidateReviewPresentationState.Default;
        }

        return result;
    }
}

/// <summary>
/// Candidateの永続化されないレビュー表示状態を保持する。
/// </summary>
/// <param name="IsReviewSkipped">現在のGroup Keepからレビュー不要と判断されたか</param>
/// <param name="ReviewSkipReason">レビュー省略理由。省略対象外ではnull</param>
internal sealed record CandidateReviewPresentationState(bool IsReviewSkipped, string? ReviewSkipReason)
{
    internal static CandidateReviewPresentationState Default { get; } = new(false, null);
}
