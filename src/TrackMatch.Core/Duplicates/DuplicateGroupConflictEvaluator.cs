using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Duplicates;

/// <summary>
/// Human Verdictの重複連結とNotDuplicateの論理矛盾を導出する。
/// </summary>
public static class DuplicateGroupConflictEvaluator
{
    /// <summary>
    /// 同じConfirmedDuplicate連結成分内に存在するNotDuplicateと、矛盾を成立させているConfirmedDuplicate経路を列挙する。
    /// </summary>
    public static IReadOnlyList<DuplicateGroupConflict> FindConflicts(IReadOnlyCollection<CandidateReview> reviews)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        var confirmed = reviews
            .Where(review => review.Decision == CandidateReviewDecision.ConfirmedDuplicate)
            .ToArray();
        var result = new List<DuplicateGroupConflict>();

        // 人間が直接「重複ではない」と判断した事実は捨てない。
        // ConfirmedDuplicateの推移と矛盾しても保存し、原因となる経路を示してユーザー自身が見直せる正常なConflict状態として扱う。
        foreach (var notDuplicate in reviews.Where(review => review.Decision == CandidateReviewDecision.NotDuplicate))
        {
            var path = FindConfirmedPath(
                notDuplicate.Pair.TrackIdA,
                notDuplicate.Pair.TrackIdB,
                confirmed);
            if (path.Count != 0)
            {
                result.Add(new DuplicateGroupConflict(notDuplicate, path));
            }
        }

        return result;
    }

    private static IReadOnlyList<CandidateReview> FindConfirmedPath(
        long start,
        long goal,
        IReadOnlyCollection<CandidateReview> confirmed)
    {
        var adjacency = new Dictionary<long, List<(long TrackId, CandidateReview Review)>>();
        foreach (var review in confirmed)
        {
            AddEdge(review.Pair.TrackIdA, review.Pair.TrackIdB, review);
            AddEdge(review.Pair.TrackIdB, review.Pair.TrackIdA, review);
        }

        var queue = new Queue<long>();
        var previous = new Dictionary<long, (long TrackId, CandidateReview Review)>();
        var visited = new HashSet<long> { start };
        queue.Enqueue(start);
        while (queue.Count != 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var next))
            {
                continue;
            }

            foreach (var edge in next)
            {
                if (!visited.Add(edge.TrackId))
                {
                    continue;
                }

                previous[edge.TrackId] = (current, edge.Review);
                if (edge.TrackId == goal)
                {
                    return RestorePath(start, goal, previous);
                }

                queue.Enqueue(edge.TrackId);
            }
        }

        return [];

        void AddEdge(long from, long to, CandidateReview review)
        {
            if (!adjacency.TryGetValue(from, out var edges))
            {
                adjacency[from] = edges = [];
            }

            edges.Add((to, review));
        }
    }

    private static IReadOnlyList<CandidateReview> RestorePath(
        long start,
        long goal,
        IReadOnlyDictionary<long, (long TrackId, CandidateReview Review)> previous)
    {
        var path = new List<CandidateReview>();
        var current = goal;
        while (current != start)
        {
            var step = previous[current];
            path.Add(step.Review);
            current = step.TrackId;
        }

        path.Reverse();
        return path;
    }
}

/// <summary>
/// NotDuplicateと、その2 Trackを同一Duplicate Groupへ接続している原因Human Verdictを保持する。
/// </summary>
public sealed record DuplicateGroupConflict(
    CandidateReview NotDuplicate,
    IReadOnlyList<CandidateReview> CauseReviews)
{
    /// <summary>Conflictの起点となるNotDuplicate Pair。</summary>
    public CandidatePairKey Pair => NotDuplicate.Pair;

    /// <summary>ユーザーが見直せる関連Human Verdictを、NotDuplicateを先頭にして返す。</summary>
    public IReadOnlyList<CandidateReview> RelatedReviews => [NotDuplicate, .. CauseReviews];
}
