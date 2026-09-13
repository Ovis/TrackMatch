using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Duplicates;

/// <summary>
/// ConfirmedDuplicateを無向グラフとして扱い、確定済み重複グループを安全に再構成する。
/// </summary>
public static class DuplicateGroupPlanner
{
    /// <summary>
    /// 現在のレビュー集合から重複グループの再構成計画を生成する。
    /// </summary>
    /// <param name="reviews">同一Libraryに属するレビューだけを渡す</param>
    /// <param name="existingGroups">同一Libraryに属する既存グループ</param>
    /// <param name="preferredKeepTrackId">今回のレビューで明示されたKeep。所属成分に存在する場合は最優先する</param>
    public static IReadOnlyList<DuplicateGroupRebuildItem> Build(
        IReadOnlyCollection<CandidateReview> reviews,
        IReadOnlyCollection<DuplicateGroup> existingGroups,
        long? preferredKeepTrackId = null)
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

        // ConfirmedDuplicateを推移的な「同一音源群」として扱うため、同じ連結成分内の
        // NotDuplicateは削除判断の根拠を矛盾させる。黙ってグループ化せず保存前に拒否する。
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

        var result = new List<DuplicateGroupRebuildItem>(components.Count);
        foreach (var component in components.OrderBy(component => component.Min()))
        {
            if (component.Count < 2)
            {
                continue;
            }

            var overlappingGroups = existingGroups
                .Where(group => group.TrackIds.Any(component.Contains))
                .Select(group => new
                {
                    Group = group,
                    Overlap = group.TrackIds.Count(component.Contains),
                })
                .ToArray();

            // 分割時に同じ既存IDを複数成分へ再利用しないため、旧Keepを含む成分だけが
            // そのグループIDを引き継げる。結合時は今回のKeepを持つ旧グループを優先する。
            var reusableGroups = overlappingGroups
                .Where(item => component.Contains(item.Group.KeepTrackId))
                .OrderByDescending(item => preferredKeepTrackId is not null && item.Group.KeepTrackId == preferredKeepTrackId)
                .ThenByDescending(item => item.Overlap)
                .ThenBy(item => item.Group.Id)
                .ToArray();
            var retainedGroup = reusableGroups.FirstOrDefault()?.Group;

            var keepTrackId = ChooseKeepTrackId(
                component,
                confirmed,
                retainedGroup,
                preferredKeepTrackId);

            result.Add(new DuplicateGroupRebuildItem(
                retainedGroup?.Id,
                keepTrackId,
                component.Order().ToArray()));
        }

        return result;
    }

    private static long ChooseKeepTrackId(
        IReadOnlySet<long> component,
        IReadOnlyCollection<CandidateReview> confirmed,
        DuplicateGroup? retainedGroup,
        long? preferredKeepTrackId)
    {
        if (preferredKeepTrackId is { } preferred && component.Contains(preferred))
        {
            return preferred;
        }

        if (retainedGroup is not null && component.Contains(retainedGroup.KeepTrackId))
        {
            return retainedGroup.KeepTrackId;
        }

        // 旧Keepを含まない側へ分割された場合にも必ず1件残す必要がある。
        // 過去のペアレビューでKeepに選ばれたTrackを優先し、同条件ならTrack IDで決定的に選ぶ。
        var reviewedKeeps = confirmed
            .Where(review => component.Contains(review.Pair.TrackIdA) && component.Contains(review.Pair.TrackIdB))
            .Select(review => review.KeepTrackId!.Value)
            .Where(component.Contains)
            .GroupBy(trackId => trackId)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => (long?)group.Key)
            .FirstOrDefault();

        return reviewedKeeps ?? component.Min();
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
