using TrackMatch.Core.Candidates;

namespace TrackMatch.App;

/// <summary>
/// ソート済みCandidate一覧へPair単位の追加・更新・削除を線形時間で反映する。
/// </summary>
internal static class CandidateListMerger
{
    /// <summary>
    /// Affected Pairの旧要素を除外し、同じ順序で取得済みのAfter要素と一度の走査でMergeする。
    /// </summary>
    public static IReadOnlyList<CandidateReviewItemViewModel> Merge(
        IReadOnlyList<CandidateReviewItemViewModel> currentSorted,
        IReadOnlySet<CandidatePairKey> affectedPairKeys,
        IReadOnlyList<CandidateReviewItemViewModel> replacementsSorted,
        Comparison<CandidateReviewItemViewModel> comparison)
    {
        ArgumentNullException.ThrowIfNull(currentSorted);
        ArgumentNullException.ThrowIfNull(affectedPairKeys);
        ArgumentNullException.ThrowIfNull(replacementsSorted);
        ArgumentNullException.ThrowIfNull(comparison);

        var replacementKeys = new HashSet<CandidatePairKey>();
        foreach (var item in replacementsSorted)
        {
            var pair = GetPair(item);
            if (!affectedPairKeys.Contains(pair))
            {
                throw new InvalidOperationException("差分取得結果にAffected Pair外のCandidateが含まれています。");
            }

            if (!replacementKeys.Add(pair))
            {
                throw new InvalidOperationException("差分取得結果に同じCandidate Pairが重複しています。");
            }
        }

        var merged = new List<CandidateReviewItemViewModel>(
            currentSorted.Count + replacementsSorted.Count);
        var currentIndex = 0;
        var replacementIndex = 0;
        while (currentIndex < currentSorted.Count || replacementIndex < replacementsSorted.Count)
        {
            while (currentIndex < currentSorted.Count
                   && affectedPairKeys.Contains(GetPair(currentSorted[currentIndex])))
            {
                currentIndex++;
            }

            if (currentIndex >= currentSorted.Count)
            {
                while (replacementIndex < replacementsSorted.Count)
                {
                    merged.Add(replacementsSorted[replacementIndex++]);
                }

                break;
            }

            if (replacementIndex >= replacementsSorted.Count)
            {
                while (currentIndex < currentSorted.Count)
                {
                    var current = currentSorted[currentIndex++];
                    if (!affectedPairKeys.Contains(GetPair(current)))
                    {
                        merged.Add(current);
                    }
                }

                break;
            }

            if (comparison(currentSorted[currentIndex], replacementsSorted[replacementIndex]) <= 0)
            {
                merged.Add(currentSorted[currentIndex++]);
            }
            else
            {
                merged.Add(replacementsSorted[replacementIndex++]);
            }
        }

        return merged;
    }

    private static CandidatePairKey GetPair(CandidateReviewItemViewModel item)
        => CandidatePairKey.Create(item.TrackIdA, item.TrackIdB);
}

/// <summary>Pair Keyと旧表示位置から差分更新後の選択を復元する。</summary>
internal static class CandidateSelectionResolver
{
    /// <summary>
    /// 同じPairが表示中なら新しいViewModelを、消失した場合は旧表示位置に最も近いCandidateを返す。
    /// </summary>
    public static CandidateReviewItemViewModel? Resolve(
        IReadOnlyDictionary<CandidatePairKey, CandidateReviewItemViewModel> candidatesByPair,
        IReadOnlyList<CandidateReviewItemViewModel> visibleCandidates,
        CandidatePairKey previousSelection,
        int previousDisplayIndex)
    {
        if (candidatesByPair.TryGetValue(previousSelection, out var samePair)
            && visibleCandidates.Contains(samePair))
        {
            return samePair;
        }

        return visibleCandidates.Count == 0
            ? null
            : visibleCandidates[Math.Min(Math.Max(previousDisplayIndex, 0), visibleCandidates.Count - 1)];
    }
}
