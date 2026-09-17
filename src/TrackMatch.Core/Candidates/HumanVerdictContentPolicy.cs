namespace TrackMatch.Core.Candidates;

/// <summary>Track Content検証結果からHuman Verdictを維持するか無効化するかを決定する。</summary>
public static class HumanVerdictContentPolicy
{
    /// <summary>Content Verification Failureの場合だけCurrent Verdictを無効化する必要がある。</summary>
    public static bool RequiresInvalidation(HumanVerdictContentVerificationState verificationState)
        => verificationState == HumanVerdictContentVerificationState.Failed;

    /// <summary>再評価推奨はHuman Verdictの有効性を変更しない。</summary>
    public static bool KeepsVerdict(HumanVerdictReevaluationState reevaluationState)
        => reevaluationState is HumanVerdictReevaluationState.Current or HumanVerdictReevaluationState.Recommended;
}
