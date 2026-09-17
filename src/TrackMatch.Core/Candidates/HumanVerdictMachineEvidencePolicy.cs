namespace TrackMatch.Core.Candidates;

/// <summary>機械解析結果とHuman Verdictが併存するときの正本選択規則を定義する。</summary>
public static class HumanVerdictMachineEvidencePolicy
{
    /// <summary>Human Verdictが存在する場合は機械分類に関係なくそれを正本として返す。</summary>
    public static CandidateReview? ResolveAuthoritativeVerdict(CandidateReview? humanVerdict)
        => humanVerdict;

    /// <summary>機械解析結果の変化だけではHuman Verdictを無効化しない。</summary>
    public static bool InvalidatesVerdictFromMachineEvidenceChange() => false;
}
