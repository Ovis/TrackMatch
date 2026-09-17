namespace TrackMatch.Core.Candidates;

/// <summary>Human VerdictをGlobal Pair単位で比較する補助処理を提供する。</summary>
public static class HumanVerdictPairComparer
{
    public static bool IsSamePair(CandidateReview verdict, long trackIdA, long trackIdB)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return verdict.Pair == CandidatePairKey.Create(trackIdA, trackIdB);
    }
}
