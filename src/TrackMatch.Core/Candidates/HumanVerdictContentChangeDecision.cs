namespace TrackMatch.Core.Candidates;

/// <summary>Track Content検証結果からHuman Verdictの処理を解決する。</summary>
public enum HumanVerdictContentChangeDecision
{
    KeepCurrent,
    InvalidateToHistory,
}

public static class HumanVerdictContentChangeDecisionResolver
{
    public static HumanVerdictContentChangeDecision Resolve(HumanVerdictContentVerificationState verificationState)
        => HumanVerdictContentPolicy.RequiresInvalidation(verificationState)
            ? HumanVerdictContentChangeDecision.InvalidateToHistory
            : HumanVerdictContentChangeDecision.KeepCurrent;
}
