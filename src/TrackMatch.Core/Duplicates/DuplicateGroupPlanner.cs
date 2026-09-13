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
    /// KeepはLibrary固有Dispositionなので、このPlannerでは選択しない。既存Group IDは、結合・分割後も
    /// 一意に引き継げる成分だけへ再利用候補として渡す。
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

        var components = BuildComponents(adjacency);
        var componentByTrack = new Dictionary<long, int>();
        for (var index = 0; index < components.Count; index++)
        {
            foreach (var trackId in components[index])
            {
                componentByTrack[trackId] = index;
            }
        }

        // ConfirmedDuplicateを推移的な同一音源関係として扱うため、同じ連結成分内のNotDuplicateは矛盾する。
        foreach (var review in reviews.Where(review => review.Decision == CandidateReviewDecision.NotDuplicate))
        {
            if (componentByTrack.TryGetValue(review.Pair.TrackIdA, out var componentA)
                && componentByTrack.TryGetValue(review.Pair.TrackIdB, out var componentB)
                && componentA == componentB)
            {
                throw new InvalidOperationException(
                    $"Track {review.Pair.TrackIdA} と {review.Pair.TrackIdB} は、別の重複確認を経由すると同じ重複グループになります。既存レビューとの矛盾を解消してください。");
            }
        }

        var reusableGroupIds = new HashSet<long>();
        var result = new List<DuplicateGroupRebuildItem>(components.Count);

        foreach (var component in components.OrderBy(component => component.Min()))
        {
            if (component.Count < 2)
            {
                continue;
            }

            // 分割では同一IDを複数成分へ複製できないため、最大Overlapの成分だけが旧IDを引き継ぐ。
            // 結合では複数旧Groupのうち最も大きく重なるGroupを代表IDとして再利用する。
            var retained = existingGroups
                .Where(group => !reusableGroupIds.Contains(group.Id))
                .Select(group => new
                {
                    Group = group,
                    Overlap = group.TrackIds.Count(component.Contains),
                })
                .Where(item => item.Overlap > 0)
                .OrderByDescending(item => item.Overlap)
                .ThenBy(item => item.Group.Id)
                .FirstOrDefault();

            if (retained is not null)
            {
                reusableGroupIds.Add(retained.Group.Id);
            }

            result.Add(new DuplicateGroupRebuildItem(
                retained?.Group.Id,
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
