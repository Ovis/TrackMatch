namespace TrackMatch.Core.Candidates;

/// <summary>
/// Current Human Verdict集合から、ConfirmedDuplicateの推移関係とNotDuplicateが衝突するTrackを検出する。
/// </summary>
public static class HumanVerdictConflictDetector
{
    /// <summary>矛盾に関与するTrack ID集合を返す。</summary>
    public static IReadOnlySet<long> FindConflictingTrackIds(IReadOnlyCollection<CandidateReview> reviews)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        var adjacency = new Dictionary<long, HashSet<long>>();
        foreach (var review in reviews.Where(item => item.Decision == CandidateReviewDecision.ConfirmedDuplicate))
        {
            review.Validate();
            Add(adjacency, review.Pair.TrackIdA, review.Pair.TrackIdB);
        }

        var componentByTrack = new Dictionary<long, int>();
        var visited = new HashSet<long>();
        var component = 0;
        foreach (var start in adjacency.Keys)
        {
            if (!visited.Add(start)) continue;
            var queue = new Queue<long>(); queue.Enqueue(start); componentByTrack[start] = component;
            while (queue.Count > 0)
            {
                foreach (var next in adjacency[queue.Dequeue()])
                {
                    if (!visited.Add(next)) continue;
                    componentByTrack[next] = component; queue.Enqueue(next);
                }
            }
            component++;
        }

        var result = new HashSet<long>();
        foreach (var review in reviews.Where(item => item.Decision == CandidateReviewDecision.NotDuplicate))
        {
            if (componentByTrack.TryGetValue(review.Pair.TrackIdA, out var a) && componentByTrack.TryGetValue(review.Pair.TrackIdB, out var b) && a == b)
            {
                result.Add(review.Pair.TrackIdA); result.Add(review.Pair.TrackIdB);
            }
        }
        return result;
    }

    private static void Add(IDictionary<long, HashSet<long>> adjacency, long a, long b)
    {
        if (!adjacency.TryGetValue(a, out var aa)) adjacency[a] = aa = [];
        if (!adjacency.TryGetValue(b, out var bb)) adjacency[b] = bb = [];
        aa.Add(b); bb.Add(a);
    }
}
