using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Duplicates;

/// <summary>
/// Keep確定のために追加レビューが必要なPairを、現在のHuman Verdictと既存Candidateから導出する。
/// </summary>
public static class ReviewNecessityEvaluator
{
    /// <summary>
    /// Keep候補同士に既存の未レビューCandidateが無い場合、補完すべきPairを必要最小限の1件だけ返す。
    /// </summary>
    /// <param name="groupTrackIds">対象Duplicate Groupの現在Track</param>
    /// <param name="reviews">現在有効なHuman Verdict</param>
    /// <param name="candidatePairs">既存Candidate Pair</param>
    public static CandidatePairKey? FindSupplementalPair(
        IReadOnlyCollection<long> groupTrackIds,
        IReadOnlyCollection<CandidateReview> reviews,
        IReadOnlyCollection<CandidatePair> candidatePairs)
    {
        ArgumentNullException.ThrowIfNull(groupTrackIds);
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(candidatePairs);

        var candidates = PreferenceGraphEvaluator.GetKeepCandidates(reviews, groupTrackIds)
            .Order()
            .ToArray();
        if (candidates.Length < 2)
        {
            return null;
        }

        var reviewed = reviews.Select(review => review.Pair).ToHashSet();
        var existing = candidatePairs
            .Select(pair => CandidatePairKey.Create(pair.TrackIdA, pair.TrackIdB))
            .ToHashSet();

        // 既存の未レビューCandidateがKeep候補同士にあるなら、まずそれをレビューすればよい。
        // 補完Pairを一括生成せず、レビュー後に候補を再計算して不要な比較を増やさない。
        for (var i = 0; i < candidates.Length - 1; i++)
        {
            for (var j = i + 1; j < candidates.Length; j++)
            {
                var pair = CandidatePairKey.Create(candidates[i], candidates[j]);
                if (!reviewed.Contains(pair) && existing.Contains(pair))
                {
                    return null;
                }
            }
        }

        for (var i = 0; i < candidates.Length - 1; i++)
        {
            for (var j = i + 1; j < candidates.Length; j++)
            {
                var pair = CandidatePairKey.Create(candidates[i], candidates[j]);
                if (!reviewed.Contains(pair))
                {
                    return pair;
                }
            }
        }

        return null;
    }
}
