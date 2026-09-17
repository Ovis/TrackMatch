namespace TrackMatch.Core.Candidates;

/// <summary>Human Verdict再設計で共有する正本原則をコード上の明示的な契約として提供する。</summary>
public static class HumanVerdictInvariant
{
    public const bool HumanDecisionOverridesMachineEvidence = true;
    public const bool MachineEvidenceChangeInvalidatesHumanDecision = false;
    public const bool ReReviewRecommendationInvalidatesHumanDecision = false;
    public const bool ContentVerificationFailureInvalidatesHumanDecision = true;
    public const bool ContradictoryHumanDecisionsArePreservedAsConflict = true;
}
