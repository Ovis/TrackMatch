using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Duplicates;

/// <summary>
/// Human Verdictの重複連結とNotDuplicateの論理矛盾を導出する。
/// </summary>
public static class DuplicateGroupConflictEvaluator
{
    /// <summary>
    /// 同じConfirmedDuplicate連結成分内に存在するNotDuplicateをConflictとして列挙する。
    /// </summary>
    public static IReadOnlyList<CandidateReview> FindConflicts(IReadOnlyCollection<CandidateReview> reviews)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        var groups = DuplicateGroupPlanner.Build(reviews, []);
        var groupByTrack = groups
            .SelectMany((group, index) => group.TrackIds.Select(trackId => (trackId, index)))
            .ToDictionary(item => item.trackId, item => item.index);

        // 人間が直接「重複ではない」と判断した事実は捨てない。
        // ConfirmedDuplicateの推移と矛盾しても保存し、ユーザーが既存Verdictを見直せる正常なConflict状態として扱う。
        return reviews
            .Where(review => review.Decision == CandidateReviewDecision.NotDuplicate)
            .Where(review => groupByTrack.TryGetValue(review.Pair.TrackIdA, out var left)
                && groupByTrack.TryGetValue(review.Pair.TrackIdB, out var right)
                && left == right)
            .ToArray();
    }
}
