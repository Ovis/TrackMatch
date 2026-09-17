using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Duplicates;

/// <summary>
/// ConfirmedDuplicate Human Verdictから向き付き優劣関係を評価する。
/// </summary>
public static class PreferenceGraphEvaluator
{
    /// <summary>
    /// 優劣関係に循環がないことを検証する。
    /// </summary>
    public static void EnsureAcyclic(IEnumerable<CandidateReview> reviews)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        var edges = BuildEdges(reviews);
        var visiting = new HashSet<long>();
        var visited = new HashSet<long>();
        foreach (var trackId in edges.Keys.Concat(edges.Values.SelectMany(value => value)).Distinct())
        {
            if (HasCycle(trackId, edges, visiting, visited))
            {
                // NotDuplicateの矛盾はConflictとして保持する一方、優劣循環はKeepの意味論を壊すため保存前に拒否する。
                throw new InvalidOperationException("この判定を保存すると残すべきTrackの優劣関係が循環します。既存レビューを見直してください。");
            }
        }
    }

    /// <summary>
    /// 指定Track集合のうち、一度も劣る側になっていないKeep候補を返す。
    /// </summary>
    public static IReadOnlyList<long> GetKeepCandidates(IEnumerable<CandidateReview> reviews, IEnumerable<long> trackIds)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(trackIds);
        var candidates = trackIds.Distinct().ToHashSet();
        foreach (var review in reviews.Where(review => review.Decision == CandidateReviewDecision.ConfirmedDuplicate))
        {
            review.Validate();
            var preferred = review.PreferredTrackId!.Value;
            var rejected = preferred == review.Pair.TrackIdA ? review.Pair.TrackIdB : review.Pair.TrackIdA;
            candidates.Remove(rejected);
        }

        return candidates.Order().ToArray();
    }

    /// <summary>
    /// Human Verdictだけからpreferredがinferiorより優れていると推移的に導出できるか判定する。
    /// </summary>
    public static bool IsPreferredTransitively(IEnumerable<CandidateReview> reviews, long preferred, long inferior)
    {
        var edges = BuildEdges(reviews);
        var visited = new HashSet<long>();
        var queue = new Queue<long>();
        queue.Enqueue(preferred);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current) || !edges.TryGetValue(current, out var next)) continue;
            if (next.Contains(inferior)) return true;
            foreach (var trackId in next) queue.Enqueue(trackId);
        }

        return false;
    }

    private static Dictionary<long, HashSet<long>> BuildEdges(IEnumerable<CandidateReview> reviews)
    {
        var result = new Dictionary<long, HashSet<long>>();
        foreach (var review in reviews.Where(review => review.Decision == CandidateReviewDecision.ConfirmedDuplicate))
        {
            review.Validate();
            var preferred = review.PreferredTrackId!.Value;
            var inferior = preferred == review.Pair.TrackIdA ? review.Pair.TrackIdB : review.Pair.TrackIdA;
            if (!result.TryGetValue(preferred, out var edges)) result[preferred] = edges = [];
            edges.Add(inferior);
        }

        return result;
    }

    private static bool HasCycle(long current, IReadOnlyDictionary<long, HashSet<long>> edges, HashSet<long> visiting, HashSet<long> visited)
    {
        if (visited.Contains(current)) return false;
        if (!visiting.Add(current)) return true;
        if (edges.TryGetValue(current, out var next))
        {
            foreach (var trackId in next)
            {
                if (HasCycle(trackId, edges, visiting, visited)) return true;
            }
        }

        visiting.Remove(current);
        visited.Add(current);
        return false;
    }
}
