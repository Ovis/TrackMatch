namespace TrackMatch.Core.Candidates;

/// <summary>Current Human Verdictの変更をHistoryへ残す条件を定義する。</summary>
public static class HumanVerdictHistoryPolicy
{
    /// <summary>判定種別だけでなくPreferred Track変更もHuman Verdict変更としてHistory対象にする。</summary>
    public static bool HasChanged(CandidateReview? before, CandidateReview? after) => !Equals(before, after);
}
