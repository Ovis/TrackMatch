using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;

namespace TrackMatch.App;

/// <summary>
/// Candidate Review Reportと現在のHuman Verdict / Duplicate Groupから、永続化しないUI上のレビュー状態を導出する。
/// </summary>
internal static class CandidateReviewPresentationStateResolver
{
    internal const string ReviewSkipReason = "この組み合わせは現在のレビュー判定から、残すファイルの決定には不要と判断できるため、レビューを省略しています";

    /// <summary>
    /// Candidateごとのレビュー省略状態を計算する。
    /// </summary>
    /// <remarks>
    /// レビュー省略はHuman Verdictではない。優劣の推移、Keep候補の脱落、一意Keep確定から毎回導出し、
    /// Human Verdictの変更や解除後に古い省略判定が残らないようDBへ保存しない。
    /// </remarks>
    /// <param name="rows">現在LibraryのCandidate Review Report</param>
    /// <param name="groups">現在Libraryへ投影されたDuplicate Group</param>
    /// <param name="reviews">現在有効なGlobal Human Verdict</param>
    internal static IReadOnlyDictionary<CandidatePairKey, CandidateReviewPresentationState> Resolve(
        IReadOnlyCollection<CandidateReviewReportRow> rows,
        IReadOnlyCollection<DuplicateGroup> groups,
        IReadOnlyCollection<CandidateReview> reviews)
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
                && CanSkipPair(groupA, pair, reviews);

            result[pair] = isSkipped
                ? new CandidateReviewPresentationState(true, ReviewSkipReason)
                : CandidateReviewPresentationState.Default;
        }

        return result;
    }

    private static bool CanSkipPair(
        DuplicateGroup group,
        CandidatePairKey pair,
        IReadOnlyCollection<CandidateReview> reviews)
    {
        if (group.KeepStatus == DuplicateGroupKeepStatus.Conflict)
        {
            return false;
        }

        // Keepが一意なら、同一Group内に残る未レビューPairはKeep決定へ影響しない。
        if (group.KeepStatus == DuplicateGroupKeepStatus.Selected)
        {
            return true;
        }

        var groupTrackIds = group.GlobalTrackIds.ToHashSet();
        var groupReviews = reviews
            .Where(review => groupTrackIds.Contains(review.Pair.TrackIdA)
                && groupTrackIds.Contains(review.Pair.TrackIdB))
            .ToArray();

        // 直接辺がなくても推移的に優劣が確定していれば、同じ判断を人へ再要求しない。
        if (PreferenceGraphEvaluator.IsPreferredTransitively(
                groupReviews,
                pair.TrackIdA,
                pair.TrackIdB)
            || PreferenceGraphEvaluator.IsPreferredTransitively(
                groupReviews,
                pair.TrackIdB,
                pair.TrackIdA))
        {
            return true;
        }

        // 複数Top候補が残っている場合でも、双方が既にTop候補から脱落していればKeep決定には不要。
        var keepCandidates = PreferenceGraphEvaluator.GetKeepCandidates(groupReviews, group.GlobalTrackIds);
        return !keepCandidates.Contains(pair.TrackIdA) && !keepCandidates.Contains(pair.TrackIdB);
    }
}

/// <summary>
/// Candidateの永続化されないレビュー表示状態を保持する。
/// </summary>
/// <param name="IsReviewSkipped">現在のHuman Verdictからレビュー不要と判断されたか</param>
/// <param name="ReviewSkipReason">レビュー省略理由。省略対象外ではnull</param>
internal sealed record CandidateReviewPresentationState(bool IsReviewSkipped, string? ReviewSkipReason)
{
    internal static CandidateReviewPresentationState Default { get; } = new(false, null);
}
