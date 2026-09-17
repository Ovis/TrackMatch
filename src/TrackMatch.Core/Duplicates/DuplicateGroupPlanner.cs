using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Duplicates;

/// <summary>
/// Global ConfirmedDuplicateを無向グラフとして扱い、Global Duplicate Groupの再構成計画を生成する。
/// </summary>
public static class DuplicateGroupPlanner
{
    /// <summary>
    /// Current Human Verdict集合からGlobal Duplicate Groupの連結成分を構成する。
    /// </summary>
    /// <remarks>
    /// NotDuplicateとの矛盾はHuman Verdictの保存自体を拒否せず、派生Group側でConflictとして扱う。
    /// このPlannerはTopologyだけを構成し、Preferred TrackやConflictの解決は上位層へ委ねる。
    /// </remarks>
    public static IReadOnlyList<DuplicateGroupRebuildItem> Build(
        IReadOnlyCollection<CandidateReview> reviews,
        IReadOnlyCollection<GlobalDuplicateGroup> existingGroups)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(existingGroups);

        var confirmed = reviews
            .Where(review => review.Decision == CandidateReviewDecision.ConfirmedDuplicate)
            .ToArray();
        var adjacency = new Dictionary<long, HashSet<long>>();

        foreach (var review in confirmed)
        {
            review.Validate();
            AddEdge(adjacency, review.Pair.TrackIdA, review.Pair.TrackIdB);
        }

        var components = BuildComponents(adjacency)
            .Where(component => component.Count >= 2)
            .OrderBy(component => component.Min())
            .ToArray();

        // Split/Merge時のGroup IDは、旧Groupと新ComponentのOverlapが最大になる組合せから優先して割り当てる。
        // Componentの処理順だけで小さい側へ旧IDが渡るとHistory追跡が不安定になるため、割当を先に全体で決める。
        var assignments = new Dictionary<int, long>();
        var usedGroupIds = new HashSet<long>();
        foreach (var candidate in existingGroups
                     .SelectMany(group => components.Select((component, index) => new
                     {
                         GroupId = group.Id,
                         ComponentIndex = index,
                         Overlap = group.TrackIds.Count(component.Contains),
                         ComponentMin = component.Min(),
                     }))
                     .Where(candidate => candidate.Overlap > 0)
                     .OrderByDescending(candidate => candidate.Overlap)
                     .ThenBy(candidate => candidate.GroupId)
                     .ThenBy(candidate => candidate.ComponentMin))
        {
            if (usedGroupIds.Contains(candidate.GroupId)
                || assignments.ContainsKey(candidate.ComponentIndex))
            {
                continue;
            }

            usedGroupIds.Add(candidate.GroupId);
            assignments[candidate.ComponentIndex] = candidate.GroupId;
        }

        var result = new List<DuplicateGroupRebuildItem>(components.Length);
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            result.Add(new DuplicateGroupRebuildItem(
                assignments.GetValueOrDefault(index) is { } groupId && groupId > 0 ? groupId : null,
                component.Order().ToArray()));
        }

        return result;
    }

    private static List<HashSet<long>> BuildComponents(IReadOnlyDictionary<long, HashSet<long>> adjacency)
    {
        var visited = new HashSet<long>();
        var components = new List<HashSet<long>>();

        foreach (var start in adjacency.Keys.Order())
        {
            if (!visited.Add(start))
            {
                continue;
            }

            var component = new HashSet<long> { start };
            var queue = new Queue<long>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var next in adjacency[current])
                {
                    if (!visited.Add(next))
                    {
                        continue;
                    }

                    component.Add(next);
                    queue.Enqueue(next);
                }
            }

            components.Add(component);
        }

        return components;
    }

    private static void AddEdge(IDictionary<long, HashSet<long>> adjacency, long left, long right)
    {
        if (!adjacency.TryGetValue(left, out var leftEdges))
        {
            leftEdges = [];
            adjacency[left] = leftEdges;
        }

        if (!adjacency.TryGetValue(right, out var rightEdges))
        {
            rightEdges = [];
            adjacency[right] = rightEdges;
        }

        leftEdges.Add(right);
        rightEdges.Add(left);
    }
}
